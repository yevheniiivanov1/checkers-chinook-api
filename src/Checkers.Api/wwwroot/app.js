"use strict";

// A deliberately thin client: every rule decision comes from the API.
//   POST /v1/position/moves  legal moves (for highlighting) and PDN validation
//   POST /v1/move/validate   checks a move and returns the position after it
//   POST /v1/move/suggest    asks the engine
//   GET  /healthz            worker status

const INITIAL = "B:W21-32:B1-12";
const SIDE_NAME = { B: "Black", W: "White" };

const state = {
  position: null,   // canonical PDN from the API
  pieces: new Map(), // square -> { side: "B" | "W", king: bool }
  sideToMove: "B",
  moves: [],         // legal moves from /v1/position/moves
  history: [],       // { position, move, by } — the position *before* each move
  selected: [],      // squares clicked so far for the move being entered
  lastMove: [],
  flipped: true,     // Black at the bottom while the human plays Black
  busy: false,
  timer: null,
};

const $ = (id) => document.getElementById(id);

async function call(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  let json = null;
  try { json = await response.json(); } catch { /* empty body */ }
  return { status: response.status, ok: response.ok, body: json };
}

function problemText(result) {
  const body = result.body || {};
  if (body.errors) {
    return Object.values(body.errors).flat().join("\n");
  }
  return `${result.status} ${body.title || ""} ${body.detail || ""}`.trim();
}

// ---------- position ----------

function parseFen(fen) {
  const [turn, ...sections] = fen.split(":");
  const pieces = new Map();
  for (const section of sections) {
    const side = section[0];
    const list = section.slice(1);
    if (!list) continue;
    for (const token of list.split(",")) {
      const king = token.startsWith("K");
      pieces.set(Number(king ? token.slice(1) : token), { side, king });
    }
  }
  return { turn, pieces };
}

async function loadPosition(pdn, { fromUser = false } = {}) {
  const result = await call("POST", "/v1/position/moves", { position: pdn });
  if (!result.ok) {
    if (fromUser) showPdnError(problemText(result));
    return false;
  }
  showPdnError("");
  state.position = result.body.position;
  state.moves = result.body.moves;
  const parsed = parseFen(state.position);
  state.sideToMove = parsed.turn;
  state.pieces = parsed.pieces;
  state.selected = [];
  $("pdn").value = state.position;
  render();
  return true;
}

function humanSide() { return $("human-side").value; }
function isHumanTurn() { return humanSide() === state.sideToMove; }
function gameOver() { return state.moves.length === 0; }

// Third occurrence of the same position: a draw. Stops engine-vs-engine games from shuffling kings forever.
function repetitions() {
  return state.history.filter((entry) => entry.position === state.position).length + 1;
}

// ---------- rendering ----------

function render() {
  const board = $("board");
  board.replaceChildren();
  const next = nextTargets();

  for (let r = 0; r < 8; r++) {
    for (let c = 0; c < 8; c++) {
      const row = state.flipped ? 7 - r : r;
      const col = state.flipped ? 7 - c : c;
      const cell = document.createElement("div");
      cell.className = "cell";
      cell.setAttribute("role", "gridcell");

      if ((row + col) % 2 === 0) {
        cell.classList.add("light");
        board.appendChild(cell);
        continue;
      }

      // Row 0 is the top rank with White at the bottom: squares 1-4 are b8, d8, f8, h8.
      const square = row * 4 + Math.floor(col / 2) + 1;
      cell.classList.add("dark");
      cell.dataset.square = square;

      const label = document.createElement("span");
      label.className = "num";
      label.textContent = square;
      cell.appendChild(label);

      const piece = state.pieces.get(square);
      if (piece) {
        const disc = document.createElement("div");
        disc.className = `piece ${piece.side === "B" ? "black" : "white"}`;
        disc.textContent = piece.king ? "♛" : "";
        cell.appendChild(disc);
        cell.setAttribute("aria-label", `${square}: ${SIDE_NAME[piece.side]} ${piece.king ? "king" : "man"}`);
      } else {
        cell.setAttribute("aria-label", `${square}: empty`);
      }

      const canMove = isHumanTurn() && !state.busy && movesFrom(square).length > 0;
      if (state.lastMove.includes(square)) cell.classList.add("last");
      if (state.selected[0] === square) cell.classList.add("selected");
      else if (canMove && state.selected.length === 0) cell.classList.add("movable");
      if (next.has(square)) cell.classList.add("target");
      if (canMove || next.has(square)) cell.classList.add("clickable");

      cell.addEventListener("click", () => onSquare(square));
      board.appendChild(cell);
    }
  }

  renderStatus();
  renderHistory();
  $("engine-move").disabled = state.busy || gameOver();
  $("undo").disabled = state.busy || state.history.length === 0;
}

function renderStatus(message, isError = false) {
  const status = $("status");
  status.style.color = isError ? "var(--bad)" : "";
  if (message) { status.textContent = message; return; }
  if (!state.position) { status.textContent = "Loading…"; return; }
  if (gameOver()) {
    const loser = SIDE_NAME[state.sideToMove];
    const winner = SIDE_NAME[state.sideToMove === "B" ? "W" : "B"];
    status.textContent = `${winner} wins: ${loser} has no legal moves.`;
    return;
  }
  if (repetitions() >= 3) {
    status.textContent = "Draw by threefold repetition.";
    return;
  }
  const who = humanSide() === state.sideToMove ? "your move" : "engine to move";
  const capture = state.moves[0].captures.length > 0 ? " Capture is compulsory." : "";
  status.textContent = `${SIDE_NAME[state.sideToMove]} to move (${who}).${capture}`;
}

function renderHistory() {
  const list = $("history");
  list.replaceChildren(...state.history.map((entry) => {
    const item = document.createElement("li");
    item.textContent = entry.move;
    if (entry.by === "engine") item.className = "engine";
    return item;
  }));
  list.scrollTop = list.scrollHeight;
}

function showPdnError(text) {
  const box = $("pdn-error");
  box.textContent = text;
  box.hidden = !text;
}

function showEngineAnswer(result) {
  $("raw").textContent = JSON.stringify(result.body, null, 2);
  const body = result.body || {};
  if (!result.ok) {
    for (const id of ["s-move", "s-score", "s-depth", "s-nodes", "s-tb"]) $(id).textContent = "–";
    $("s-time").textContent = `HTTP ${result.status}`;
    $("s-pv").textContent = problemText(result);
    return;
  }
  const info = body.info || {};
  const wdl = { 1: "win", 0: "draw", "-1": "loss" };
  $("s-move").textContent = body.bestMove;
  $("s-score").textContent = info.tablebaseHit ? `${wdl[body.scoreOrWDL]} (WDL)` : body.scoreOrWDL;
  $("s-depth").textContent = body.depth;
  $("s-nodes").textContent = Number(body.nodes).toLocaleString("en-US");
  $("s-tb").textContent = info.tablebaseHit ? "hit" : "no";
  $("s-time").textContent = `${info.timeMs} ms${info.cached ? " (cached)" : ""}`;
  $("s-pv").textContent = body.pv && body.pv.length ? `pv: ${body.pv.join(" ")}` : "";
}

// ---------- move entry ----------

function movesFrom(square) {
  return state.moves.filter((m) => m.path[0] === square);
}

function candidates() {
  return state.moves.filter((m) => state.selected.every((sq, i) => m.path[i] === sq));
}

function nextTargets() {
  if (state.selected.length === 0) return new Set();
  return new Set(candidates()
    .filter((m) => m.path.length > state.selected.length)
    .map((m) => m.path[state.selected.length]));
}

function onSquare(square) {
  if (state.busy || !isHumanTurn() || gameOver()) return;

  if (state.selected.length > 0) {
    // Next landing square of the move being entered...
    if (nextTargets().has(square)) {
      state.selected.push(square);
      const matching = candidates();
      const complete = matching.find((m) => m.path.length === state.selected.length);
      if (complete && matching.length === 1) { playHumanMove(complete); return; }
      render();
      return;
    }
    // ...or straight to the final square when only one route ends there.
    const direct = candidates().filter((m) => m.path[m.path.length - 1] === square);
    if (direct.length === 1) { playHumanMove(direct[0]); return; }
  }

  state.selected = movesFrom(square).length > 0 ? [square] : [];
  render();
}

async function playHumanMove(move) {
  state.busy = true;
  render();
  const result = await call("POST", "/v1/move/validate", { position: state.position, move: move.move });
  state.busy = false;
  if (!result.ok || !result.body.legal) {
    state.selected = [];
    render();
    renderStatus(`Rejected: ${result.ok ? result.body.reason : problemText(result)}`, true);
    return;
  }
  await commitMove(result.body.move, result.body.resultPosition, "human", move.path);
}

async function commitMove(notation, resultPosition, by, path) {
  state.history.push({ position: state.position, move: notation, by });
  state.lastMove = path;
  await loadPosition(resultPosition);
  scheduleEngine();
}

// ---------- engine ----------

function scheduleEngine() {
  clearTimeout(state.timer);
  if (gameOver() || repetitions() >= 3 || !$("auto-reply").checked || isHumanTurn()) return;
  // In engine-vs-engine mode leave a moment to see each move.
  state.timer = setTimeout(engineMove, humanSide() === "-" ? 500 : 60);
}

async function engineMove() {
  if (state.busy || gameOver()) return;
  state.busy = true;
  render();
  renderStatus(`Engine (${$("level").value}) is thinking…`);

  const position = state.position;
  const result = await call("POST", "/v1/move/suggest", {
    gameId: "checkers-8x8",
    state: { notation: "PDN", position },
    level: $("level").value,
  });
  showEngineAnswer(result);

  if (!result.ok) {
    state.busy = false;
    render();
    renderStatus(`Engine request failed: ${problemText(result)}`, true);
    return;
  }

  // Double-check the engine's move with the rules endpoint, as a client would.
  const check = await call("POST", "/v1/move/validate", { position, move: result.body.bestMove });
  state.busy = false;
  if (!check.ok || !check.body.legal) {
    render();
    renderStatus(`Engine move ${result.body.bestMove} was rejected by /v1/move/validate!`, true);
    return;
  }

  const played = state.moves.find((m) => m.move === check.body.move);
  await commitMove(check.body.move, check.body.resultPosition, "engine", played ? played.path : []);
}

// ---------- controls ----------

async function newGame(pdn = INITIAL) {
  clearTimeout(state.timer);
  state.history = [];
  state.lastMove = [];
  if (await loadPosition(pdn, { fromUser: true })) scheduleEngine();
}

async function undo() {
  clearTimeout(state.timer);
  if (state.history.length === 0) return;
  let entry = state.history.pop();
  // Take back the engine's reply too, so it is the human's turn again.
  while (state.history.length > 0 && humanSide() !== "-" && entry.by === "engine") {
    entry = state.history.pop();
  }
  state.lastMove = [];
  await loadPosition(entry.position);
}

async function refreshHealth() {
  const badge = $("health");
  try {
    const result = await call("GET", "/healthz");
    const body = result.body || {};
    if (result.ok && body.ok) {
      badge.className = "badge badge-ok";
      const tb = body.tablebasePieces ? ` · tablebase ≤${body.tablebasePieces} pieces` : " · no tablebase";
      badge.textContent = `${body.engineName} · ${body.workers}/${body.configuredWorkers} workers${tb}`;
    } else {
      badge.className = "badge badge-bad";
      badge.textContent = `engine unavailable (${body.workers ?? 0} workers)`;
    }
  } catch {
    badge.className = "badge badge-bad";
    badge.textContent = "API unreachable";
  }
}

$("engine-move").addEventListener("click", () => { clearTimeout(state.timer); engineMove(); });
$("undo").addEventListener("click", undo);
$("flip").addEventListener("click", () => { state.flipped = !state.flipped; render(); });
$("new-game").addEventListener("click", () => newGame());
$("load-pdn").addEventListener("click", () => newGame($("pdn").value));
$("pdn").addEventListener("keydown", (e) => { if (e.key === "Enter") newGame($("pdn").value); });
$("samples").addEventListener("change", (e) => { if (e.target.value) newGame(e.target.value); e.target.value = ""; });
$("human-side").addEventListener("change", () => {
  state.flipped = humanSide() === "B";
  state.selected = [];
  render();
  scheduleEngine();
});
$("auto-reply").addEventListener("change", scheduleEngine);

refreshHealth();
setInterval(refreshHealth, 15000);
newGame();
