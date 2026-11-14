using System.Runtime.InteropServices;
using Aperture.Core.Library;
using Aperture.Core.Storage;

// One move, as Move to… makes it, in a process that isn't long-path aware (see the csproj).
// Arguments: <data folder> <file> <destination folder> <rename | copy>. Prints whether this process takes long paths as
// they are, then the move's outcome.
using var database = new ApertureDatabase(args[0]);
database.Initialize();
var moves = new FileMoves(database) { ForceCopy = args[3] == "copy" };
Console.WriteLine(RtlAreLongPathsEnabled() != 0 ? "long-path aware" : "not long-path aware");
Console.WriteLine(moves.Move(args[1], args[2]).Outcome);

[DllImport("ntdll.dll")]
static extern byte RtlAreLongPathsEnabled();
