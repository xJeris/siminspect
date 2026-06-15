using Lunaris.Config;

namespace SimInspect
{
    public class LunarisConfig
    {
        [ConfigSection("General")]
        [ConfigDescription("Key to toggle the Sim Inspector window (e.g. F8, F9, BackQuote).")]
        public string ToggleKey = "F8";
    }
}
