using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class XtreamLiveStreamTests
    {
        [Fact]
        public void ExposesConsumerLifecycleMethodsForEmbyRuntimeCompatibility()
        {
            var type = typeof(XtreamLiveStream);

            var addConsumer = type.GetMethod("AddConsumer", new[] { typeof(string) });
            var removeConsumer = type.GetMethod("RemoveConsumer", new[] { typeof(string) });

            Assert.NotNull(addConsumer);
            Assert.NotNull(removeConsumer);
            Assert.True(addConsumer.IsVirtual);
            Assert.True(removeConsumer.IsVirtual);
        }

        [Fact]
        public void AddConsumerAndRemoveConsumerMaintainConsumerCount()
        {
            using (var stream = CreateStream())
            {
                stream.AddConsumer("consumer-a");
                stream.AddConsumer("consumer-b");

                Assert.Equal(2, stream.ConsumerCount);

                stream.RemoveConsumer("consumer-b");
                stream.RemoveConsumer("consumer-a");
                stream.RemoveConsumer("consumer-nonexistent");

                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Fact]
        public void ConsumerLifecycleMethodsAreThreadSafe()
        {
            using (var stream = CreateStream())
            {
                const int threadCount = 20;
                const int operationsPerThread = 1000;
                using (var startBarrier = new Barrier(threadCount + 1))
                using (var postAddBarrier = new Barrier(threadCount + 1))
                using (var removeBarrier = new Barrier(threadCount + 1))
                {
                    var threads = Enumerable.Range(0, threadCount)
                        .Select(_ => new Thread(() =>
                        {
                            startBarrier.SignalAndWait();
                            for (var i = 0; i < operationsPerThread; i++)
                            {
                                stream.AddConsumer("test");
                            }

                            postAddBarrier.SignalAndWait();
                            removeBarrier.SignalAndWait();
                            for (var i = 0; i < operationsPerThread; i++)
                            {
                                stream.RemoveConsumer("test");
                            }
                        }))
                        .ToArray();

                    foreach (var thread in threads) thread.Start();
                    startBarrier.SignalAndWait();
                    postAddBarrier.SignalAndWait();
                    var consumerCountAfterAdds = stream.ConsumerCount;
                    removeBarrier.SignalAndWait();
                    foreach (var thread in threads) thread.Join();

                    Assert.Equal(threadCount * operationsPerThread, consumerCountAfterAdds);
                }

                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Fact]
        public void AddConsumerAndRemoveConsumerAreNowOnILiveStreamInterface()
        {
            // Emby 4.10.0.17 added AddConsumer(string) and RemoveConsumer(string)
            // to ILiveStream. The plugin methods satisfy the interface naturally.
            var interfaceMethodNames = typeof(ILiveStream).GetMethods().Select(method => method.Name).ToArray();

            Assert.Contains("AddConsumer", interfaceMethodNames);
            Assert.Contains("RemoveConsumer", interfaceMethodNames);
            Assert.NotNull(typeof(XtreamLiveStream).GetMethod("AddConsumer", new[] { typeof(string) }));
            Assert.NotNull(typeof(XtreamLiveStream).GetMethod("RemoveConsumer", new[] { typeof(string) }));
        }

        [Fact]
        public void ConcurrentSessionsGetUniqueMediaSourceIds()
        {
            // Two XtreamLiveStream instances for the same base channel id must have
            // different UniqueIds, which GetChannelStream appends to MediaSource.Id.
            // Without this, Emby's MediaSourceManager reuses the first live stream
            // for both sessions and the first session's close kills the second (issue #43).
            var baseId = "xtream_live_363";

            using (var stream1 = CreateStream(baseId))
            using (var stream2 = CreateStream(baseId))
            {
                Assert.NotEqual(stream1.UniqueId, stream2.UniqueId);
                Assert.NotEqual(
                    XtreamTunerHost.BuildSessionMediaSourceId(stream1.MediaSource.Id, stream1.UniqueId),
                    XtreamTunerHost.BuildSessionMediaSourceId(stream2.MediaSource.Id, stream2.UniqueId));
            }
        }

        private static XtreamLiveStream CreateStream()
        {
            return new XtreamLiveStream(
                new MediaSourceInfo
                {
                    Id = "stream-1",
                    Path = "http://example.invalid/live.ts"
                },
                "tuner-1",
                new System.Net.Http.HttpClient());
        }

        private static XtreamLiveStream CreateStream(string baseId)
        {
            return new XtreamLiveStream(
                new MediaSourceInfo
                {
                    Id = baseId,
                    Path = "http://example.invalid/live.ts"
                },
                "tuner-1",
                new System.Net.Http.HttpClient());
        }
    }
}