using System.Text;
using Checkers.EngineHost;

// Must run before any engine DLL is loaded; see StdStreams.
var protocol = StdStreams.TakeProtocolOutput();
var output = new StreamWriter(protocol, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };

new WorkerHost(output).Run(Console.OpenStandardInput());
return 0;
