﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Azure.WebJobs.Host.TestCommon
{
    public static class JobHostFactory
    {
        public static JobHost<TProgram> Create<TProgram>()
        {
            return Create<TProgram>(maxDequeueCount: 5);
        }

        public static JobHost<TProgram> Create<TProgram>(int maxDequeueCount)
        {
            IHost host = new HostBuilder()
                .ConfigureDefaultTestHost<TProgram>()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IHostIdProvider>(new FixedHostIdProvider("test"));
                    /*
                    services.AddSingleton<IStorageAccountProvider>(p => new SimpleStorageAccountProvider(p.GetRequiredService<StorageClientFactory>())
                    {
                        StorageAccount = storageAccount,
                        // use null logging string since unit tests don't need logs.
                        DashboardAccount = null
                    });*/ // $$$
                })
                .Build();

            return host.GetJobHost<TProgram>();
        }
    }
}
