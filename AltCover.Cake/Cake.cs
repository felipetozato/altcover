﻿using Cake.Core;
using Cake.Core.Annotations;
using LogLevel = Cake.Core.Diagnostics.LogLevel;
using Verbosity = Cake.Core.Diagnostics.Verbosity;

namespace AltCover.Cake
{
    public static class Api
    {
        private static LogArgs MakeLog(ICakeContext context, LogArgs l)
        {
            if (l != null)
                return l;

            if (context != null)
                return new LogArgs()
                {
                    Info = x => context.Log.Write(Verbosity.Normal, LogLevel.Information, x),
                    Warn = x => context.Log.Write(Verbosity.Normal, LogLevel.Warning, x),
                    Echo = x => context.Log.Write(Verbosity.Normal, LogLevel.Error, x),
                    Error = x => context.Log.Write(Verbosity.Verbose, LogLevel.Information, x),
                };

            return new LogArgs()
            {
                Info = x => { },
                Warn = x => { },
                Echo = x => { },
                Error = x => { }
            };
        }

        [CakeMethodAlias]
        public static int Prepare(this ICakeContext context, PrepareArgs p, LogArgs l = null)
        {
            return CSApi.Prepare(p, MakeLog(context, l));
        }

        [CakeMethodAlias]
        public static int Collect(this ICakeContext context, CollectArgs c, LogArgs l = null)
        {
            return CSApi.Collect(c, MakeLog(context, l));
        }

        [CakeMethodAlias]
        public static string Ipmo(this ICakeContext context)
        {
            return CSApi.Ipmo();
        }

        [CakeMethodAlias]
        public static string Version(this ICakeContext context)
        {
            return CSApi.Version();
        }
    }
}