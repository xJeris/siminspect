using System;

namespace SimInspect
{
    public static class Log
    {
        public static Action<string> Info = _ => { };
        public static Action<string> Warning = _ => { };
        public static Action<string> Error = _ => { };
    }
}
