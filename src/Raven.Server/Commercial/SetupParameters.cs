﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Server.Config;
using Raven.Server.ServerWide;
using Raven.Server.Utils.Cli;
using Sparrow.Platform;

namespace Raven.Server.Commercial
{
    public class SetupParameters
    {
        public int? FixedServerPortNumber { get;set; }
        
        public bool IsPosix { get; set; }

        public static SetupParameters Get(ServerStore serverStore)
        {
            var result = new SetupParameters();
            DetermineFixedPortNumber(serverStore, result);
            result.IsPosix = PlatformDetails.RunningOnPosix;
            return result;
        }

        private static void DetermineFixedPortNumber(ServerStore serverStore, SetupParameters result)
        {
            var serverUrlKey = RavenConfiguration.GetKey(x => x.Core.ServerUrls);
            var arguments = serverStore.Configuration.CommandLineSettings?.Args;
            if (arguments == null)
                return;

            if (CommandLineConfigurationArgumentsHelper.IsConfigurationKeyInCliArgs(serverUrlKey, arguments))
            {
                Uri.TryCreate(serverStore.Configuration.Core.ServerUrls[0], UriKind.Absolute, out var uri);
                result.FixedServerPortNumber = uri.Port;
            }
        }
    }
}
