using BabySharkBot.Setup;

namespace BabySharkBot.Services
{
    // Mining-test log file naming (owner spec 2026-10-23): every telemetry
    // file written under data/mining_tests carries the start-location tag
    // (NW/SE) resolved at OnStart into Globals.currentMapStartLocation, so
    // runs from the two spawns can never be mixed up. The loggers are
    // constructed BEFORE OnStart resolves the spawn, so each one resolves
    // its file name LAZILY on first write — by then the tag is set.
    public static class MiningTestLogNaming
    {
        // "_NW" / "_SE", or "" when the start location has not been resolved
        // yet — the file name then stays exactly what it was before the tag
        // existed.
        public static string TagForFileName()
        {
            var tag = Globals.currentMapStartLocation;
            return string.IsNullOrEmpty(tag) ? string.Empty : "_" + tag;
        }
    }
}
