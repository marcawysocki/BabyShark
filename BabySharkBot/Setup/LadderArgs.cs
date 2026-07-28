using System;

namespace BabySharkBot.Setup
{
    public class LadderArgs
    {
        public string MapName { get; private set; }
        public int GamePort { get; private set; }
        public int StartPort { get; private set; }
        public string LadderServer { get; private set; }

        public LadderArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                switch (arg)
                {
                    case "-m":
                    case "--map":
                        if (i + 1 < args.Length)
                            MapName = args[++i];
                        break;
                    case "-g":
                    case "--gameport":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int gamePort))
                            GamePort = gamePort;
                        break;
                    case "-o":
                    case "--startport":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int startPort))
                            StartPort = startPort;
                        break;
                    case "-l":
                    case "--ladderserver":
                        if (i + 1 < args.Length)
                            LadderServer = args[++i];
                        break;
                }
            }
        }
    }
}
