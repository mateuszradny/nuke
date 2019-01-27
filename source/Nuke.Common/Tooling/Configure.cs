// Copyright 2018 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using Nuke.Common.Utilities.Collections;

namespace Nuke.Common.Tooling
{
    public delegate T Configure<T>(T settings);
    public delegate IEnumerable<T> CombinatorialConfigure<T>(T settings);

    public static class ConfigureExtensions
    {
        public static T InvokeSafe<T>([CanBeNull] this Configure<T> configurator, T obj)
        {
            return (configurator ?? (x => x)).Invoke(obj);
        }

        public static IReadOnlyCollection<(TSettings Settings, IReadOnlyCollection<Output> Output)> Execute<TSettings>(
            this CombinatorialConfigure<TSettings> configurator,
            Func<TSettings, IReadOnlyCollection<Output>> executor,
            Action<OutputType, string> logger,
            int degreeOfParallelism,
            bool stopOnFirstError)
            where TSettings : ToolSettings, new()
        {
            return Execute(
                configurator,
                x => (Settings: x, Output: executor(x)),
                x => x.Output,
                logger,
                degreeOfParallelism,
                stopOnFirstError);
        }
        
        public static IReadOnlyCollection<(TSettings Settings, TResult Result, IReadOnlyCollection<Output> Output)> Execute<TSettings, TResult>(
            this CombinatorialConfigure<TSettings> configurator,
            Func<TSettings, (TResult Result, IReadOnlyCollection<Output> Output)> executor,
            Action<OutputType, string> logger,
            int degreeOfParallelism,
            bool stopOnFirstError)
            where TSettings : ToolSettings, new()
        {
            return Execute(
                    configurator,
                    x => (Settings: x, ReturnValue: executor(x)),
                    x => x.ReturnValue.Output,
                    logger,
                    degreeOfParallelism,
                    stopOnFirstError)
                .Select(x => (x.Settings, x.ReturnValue.Result, x.ReturnValue.Output)).ToList();
        }

        private static IReadOnlyCollection<TResult> Execute<TSettings, TResult>(
            CombinatorialConfigure<TSettings> configurator,
            Func<TSettings, TResult> executor,
            Func<TResult, IReadOnlyCollection<Output>> outputSelector,
            Action<OutputType, string> logger,
            int degreeOfParallelism,
            bool stopOnFirstError)
            where TSettings : ToolSettings, new()
        {
            var singleExecution = degreeOfParallelism == 1;

            var invocations = new ConcurrentBag<(TSettings Settings, TResult Result, Exception Exception)>();

            try
            {
                configurator(new TSettings())
                    .AsParallel()
                    .WithDegreeOfParallelism(degreeOfParallelism)
                    .ForAll(x =>
                    {
                        try
                        {
                            invocations.Add((x, executor(x.SetLogOutput(singleExecution)), default));
                        }
                        catch (Exception exception)
                        {
                            invocations.Add((x, default, exception));
                            
                            if (stopOnFirstError)
                                throw;
                        }
                    });
                
                if (invocations.Any(x => x.Exception != null))
                    throw new AggregateException(invocations.Select(x => x.Exception).WhereNotNull());

                return invocations.Select(x => x.Result).ToList();
            }
            finally
            {
                invocations
                    .Where(x => x.Settings.LogOutput)
                    .SelectMany(x =>
                        !(x.Exception is ProcessException processException)
                            ? outputSelector(x.Result)
                            : processException.Process.Output)
                    .ForEach(x => logger(x.Type, x.Text));
            }
        }

    }
}
