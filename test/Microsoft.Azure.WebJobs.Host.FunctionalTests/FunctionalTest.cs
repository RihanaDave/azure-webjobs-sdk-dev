﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.FunctionalTests.TestDoubles;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Host.Loggers;
using Microsoft.Azure.WebJobs.Host.TestCommon;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Azure.WebJobs.Host.FunctionalTests
{
    internal class FunctionalTest
    {
        private readonly IJobActivator _jobActivator;

        public FunctionalTest()
        {
            var hostBuilder = new HostBuilder();
            var host = hostBuilder.Build();
            var serviceProvider = (IServiceProvider)host.Services.GetService(typeof(IServiceProvider));
            _jobActivator = new DefaultJobActivator(serviceProvider);
        }

        public void Call(Type programType, MethodInfo method,
            IDictionary<string, object> arguments, params Type[] cloudBlobStreamBinderTypes)
        {
            // Arrange
            TaskCompletionSource<object> backgroundTaskSource = new TaskCompletionSource<object>();
            IHost host = CreateConfigurationForManualCompletion<object>(
                programType, backgroundTaskSource, cloudBlobStreamBinderTypes: cloudBlobStreamBinderTypes);
            Task backgroundTask = backgroundTaskSource.Task;

            using (host)
            {
                var jobHost = host.GetJobHost();
                Task task = jobHost.CallAsync(method, arguments);

                // Act
                bool completed = Task.WhenAny(task, backgroundTask).WaitUntilCompleted(3 * 1000);

                // Assert
                Assert.True(completed);

                // Give a nicer test failure message for faulted tasks.
                if (backgroundTask.Status == TaskStatus.Faulted)
                {
                    backgroundTask.GetAwaiter().GetResult();
                }

                // The background task should not complete.
                Assert.Equal(TaskStatus.WaitingForActivation, backgroundTask.Status);

                // Give a nicer test failure message for faulted tasks.
                if (task.Status == TaskStatus.Faulted)
                {
                    task.GetAwaiter().GetResult();
                }

                Assert.Equal(TaskStatus.RanToCompletion, task.Status);
            }
        }

        // Stops running the host as soon as the program marks the task as completed.
        public TResult Call<TResult>(Type programType, MethodInfo method,
            IDictionary<string, object> arguments, Action<TaskCompletionSource<TResult>> setTaskSource)
        {
            // Arrange
            TaskCompletionSource<TResult> taskSource = new TaskCompletionSource<TResult>();
            var serviceProvider = CreateConfigurationForManualCompletion<TResult>(programType,
                taskSource);
            Task<TResult> task = taskSource.Task;
            setTaskSource.Invoke(taskSource);

            try
            {
                using (serviceProvider)
                {
                    var host = serviceProvider.GetJobHost();
                    Task callTask = host.CallAsync(method, arguments);

                    // Act
                    bool completed = Task.WhenAll(task, callTask).WaitUntilCompleted(3 * 1000);

                    // Assert
                    Assert.True(completed);

                    // Give a nicer test failure message for faulted tasks.
                    if (task.Status == TaskStatus.Faulted)
                    {
                        task.GetAwaiter().GetResult();
                    }

                    if (callTask.Status == TaskStatus.Faulted)
                    {
                        callTask.GetAwaiter().GetResult();
                    }

                    Assert.Equal(TaskStatus.RanToCompletion, callTask.Status);

                    return task.Result;
                }
            }
            finally
            {
                setTaskSource.Invoke(null);
            }
        }

        // Stops running the host as soon as the program marks the task as completed.
        public Exception CallFailure(Type programType, MethodInfo method,
            IDictionary<string, object> arguments)
        {
            // Arrange
            TaskCompletionSource<object> backgroundTaskSource = new TaskCompletionSource<object>();
            var host = CreateConfigurationForCallFailure(programType,
                backgroundTaskSource);
            Task backgroundTask = backgroundTaskSource.Task;

            using (host)
            {
                Task callTask = host.GetJobHost().CallAsync(method, arguments);

                // Act
                bool completed = Task.WhenAny(callTask, backgroundTask).WaitUntilCompleted(3 * 1000);

                // Assert
                Assert.True(completed);

                // Give a nicer test failure message for faulted tasks.
                if (backgroundTask.Status == TaskStatus.Faulted)
                {
                    backgroundTask.GetAwaiter().GetResult();
                }

                Assert.Equal(TaskStatus.Faulted, callTask.Status);
                return callTask.Exception.InnerException;
            }
        }

        public IHost CreateConfigurationForCallFailure(Type programType, TaskCompletionSource<object> taskSource)
        {
            return CreateConfiguration<object>(programType, taskSource, new NullFunctionInstanceLogger());
        }

        private IHost CreateConfigurationForInstanceFailure(Type programType, TaskCompletionSource<Exception> taskSource)
        {
            return CreateConfiguration<Exception>(programType, taskSource, 
                new ExpectInstanceFailureTaskFunctionInstanceLogger(taskSource));
        }

        public IHost CreateConfigurationForInstanceSuccess(
            Type programType, TaskCompletionSource<object> taskSource, IExtensionRegistry extensions = null)
        {
            return CreateConfiguration<object>(programType, taskSource,
                new ExpectInstanceSuccessTaskFunctionInstanceLogger(taskSource), extensions);
        }

        public IHost CreateConfigurationForManualCompletion<TResult>(
            Type programType, TaskCompletionSource<TResult> taskSource, IExtensionRegistry extensions = null, params Type[] cloudBlobStreamBinderTypes)
        {
            IEnumerable<string> ignoreFailureFunctionIds = null;
            return CreateConfigurationForManualCompletion<TResult>(programType, taskSource,
                ignoreFailureFunctionIds, extensions, cloudBlobStreamBinderTypes);
        }

        private IHost CreateConfigurationForManualCompletion<TResult>(
            Type programType, TaskCompletionSource<TResult> taskSource,
            IEnumerable<string> ignoreFailureFunctions, IExtensionRegistry extensions = null, params Type[] cloudBlobStreamBinderTypes)
        {
            return CreateConfigurationForManualCompletion<TResult>(programType,
                _jobActivator, taskSource, ignoreFailureFunctions, extensions);
        }

        private static IHost CreateConfigurationForManualCompletion<TResult>(
            Type programType, IJobActivator activator,
            TaskCompletionSource<TResult> taskSource, IEnumerable<string> ignoreFailureFunctions, IExtensionRegistry extensions = null)
        {
            return CreateConfiguration<TResult>(programType, activator, taskSource,
                new ExpectManualCompletionFunctionInstanceLogger<TResult>(taskSource, false, ignoreFailureFunctions), extensions);
        }

        private IHost CreateConfiguration<TResult>(Type programType, TaskCompletionSource<TResult> taskSource,
            IFunctionInstanceLogger functionInstanceLogger, IExtensionRegistry extensions = null)
        {
            return CreateConfiguration<TResult>(programType,
                _jobActivator, taskSource, functionInstanceLogger, extensions);
        }

        private static IHost CreateConfiguration<TResult>(Type programType,
            IJobActivator activator, TaskCompletionSource<TResult> taskSource,
            IFunctionInstanceLogger functionInstanceLogger, IExtensionRegistry extensions = null)
        {
            IWebJobsExceptionHandler exceptionHandler = new TaskBackgroundExceptionHandler<TResult>(taskSource);

            return new HostBuilder()
                .ConfigureDefaultTestHost(b =>
                {
                    b.UseHostId(Guid.NewGuid().ToString("N"));
                }, programType)
                .ConfigureServices(services =>
                {
                    // services.AddSingleton<IOptionsFactory<JobHostQueuesOptions>, FakeQueuesOptionsFactory>(); $$$ ???

                    services.AddSingletonIfNotNull(activator);
                    services.AddSingletonIfNotNull(exceptionHandler);
                    services.AddSingletonIfNotNull(extensions);

                    services.AddSingleton<IFunctionInstanceLoggerProvider>(new NullFunctionInstanceLoggerProvider(functionInstanceLogger));
                    services.AddSingleton<IHostInstanceLoggerProvider>(new NullHostInstanceLoggerProvider());
                    services.AddSingleton<IFunctionOutputLoggerProvider>(new NullFunctionOutputLoggerProvider());
                })
                .Build();
        }

        // Stops running the host as soon as the first function logs completion.
        public void RunTrigger(Type programType, IExtensionRegistry extensions = null)
        {
            // Arrange
            TaskCompletionSource<object> taskSource = new TaskCompletionSource<object>();
            var serviceProvider = CreateConfigurationForInstanceSuccess(programType, taskSource, extensions);

            // Act & Assert
            RunTrigger<object>(serviceProvider, taskSource.Task);
        }

        // Stops running the host as soon as the program marks the task as completed.
        public TResult RunTrigger<TResult>(Type programType, Action<TaskCompletionSource<TResult>> setTaskSource)
        {
            return RunTrigger<TResult>(programType, setTaskSource, _jobActivator,
                ignoreFailureFunctions: null);
        }

        public TResult RunTrigger<TResult>(Type programType,
            IJobActivator activator, Action<TaskCompletionSource<TResult>> setTaskSource)
        {
            return RunTrigger<TResult>(programType, setTaskSource, activator, ignoreFailureFunctions: null);
        }

        public TResult RunTrigger<TResult>(Type programType,
            Action<TaskCompletionSource<TResult>> setTaskSource, IEnumerable<string> ignoreFailureFunctions)
        {
            return RunTrigger<TResult>(programType, setTaskSource, _jobActivator, ignoreFailureFunctions);
        }

        public TResult RunTrigger<TResult>(Type programType,
            Action<TaskCompletionSource<TResult>> setTaskSource, IJobActivator activator,
            IEnumerable<string> ignoreFailureFunctions)
        {
            // Arrange
            TaskCompletionSource<TResult> taskSource = new TaskCompletionSource<TResult>();
            var serviceProvider = CreateConfigurationForManualCompletion<TResult>(programType,
                activator, taskSource, ignoreFailureFunctions);
            Task<TResult> task = taskSource.Task;
            setTaskSource.Invoke(taskSource);

            try
            {
                // Act & Assert
                return RunTrigger<TResult>(serviceProvider, task);
            }
            finally
            {
                setTaskSource.Invoke(null);
            }
        }

        public TResult RunTrigger<TResult>(IHost host, Task<TResult> task)
        {
            // Arrange
            bool completed;

            using (host)
            {
                host.Start();

                // Act
                if (Debugger.IsAttached)
                {
                    task.WaitUntilCompleted();
                    completed = true;
                }
                else
                {
                    completed = task.WaitUntilCompleted(25 * 1000);
                }

                // Assert
                Assert.True(completed);

                // Give a nicer test failure message for faulted tasks.
                if (task.Status == TaskStatus.Faulted)
                {
                    task.GetAwaiter().GetResult();
                }

                Assert.Equal(TaskStatus.RanToCompletion, task.Status);
                return task.Result;
            }
        }

        // Stops running the host as soon as the first function logs completion.
        public Exception RunTriggerFailure(Type programType)
        {
            // Arrange
            TaskCompletionSource<Exception> taskSource = new TaskCompletionSource<Exception>();
            IHost host = CreateConfigurationForInstanceFailure(programType, taskSource);

            // The task for failed function invocation (should complete successfully with a non-null exception).
            Task<Exception> task = taskSource.Task;

            using (host)
            {
                host.Start();

                // Act
                bool completed = task.WaitUntilCompleted(3 * 1000);

                // Assert
                Assert.True(completed);

                // Give a nicer test failure message for faulted tasks.
                if (task.Status == TaskStatus.Faulted)
                {
                    task.GetAwaiter().GetResult();
                }

                Assert.Equal(TaskStatus.RanToCompletion, task.Status);
                Exception exception = task.Result;
                Assert.IsType<FunctionInvocationException>(exception);
                return exception.InnerException;
            }
        }

        // Stops running the host as soon as the program marks the task as completed.
        public Exception RunTriggerFailure<TResult>(Type programType,
            Action<TaskCompletionSource<TResult>> setTaskSource)
        {
            // Arrange
            TaskCompletionSource<Exception> failureTaskSource = new TaskCompletionSource<Exception>();
            IHost host = CreateConfigurationForInstanceFailure(programType, failureTaskSource);
            TaskCompletionSource<TResult> successTaskSource = new TaskCompletionSource<TResult>();

            // The task for failed function invocation (should complete successfully with an exception).
            Task<Exception> failureTask = failureTaskSource.Task;

            // The task for successful function invocation (should not complete).
            Task<TResult> successTask = successTaskSource.Task;
            setTaskSource.Invoke(successTaskSource);

            try
            {
                using (host)
                {
                    host.Start();

                    // Act
                    bool completed = Task.WhenAny(failureTask, successTask).WaitUntilCompleted(300 * 1000);

                    // Assert
                    Assert.True(completed);

                    // Give a nicer test failure message for faulted tasks.
                    if (successTask.Status == TaskStatus.Faulted)
                    {
                        successTask.GetAwaiter().GetResult();
                    }

                    // The function should not be invoked.
                    Assert.Equal(TaskStatus.WaitingForActivation, successTask.Status);

                    // Give a nicer test failure message for faulted tasks.
                    if (failureTask.Status == TaskStatus.Faulted)
                    {
                        failureTask.GetAwaiter().GetResult();
                    }

                    Assert.Equal(TaskStatus.RanToCompletion, failureTask.Status);
                    Exception exception = failureTask.Result;
                    Assert.IsType<FunctionInvocationException>(exception);
                    return exception.InnerException;
                }
            }
            finally
            {
                setTaskSource.Invoke(null);
            }
        }
    }
}