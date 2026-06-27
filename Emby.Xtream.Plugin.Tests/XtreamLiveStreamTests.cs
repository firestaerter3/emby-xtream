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

            var addConsumer = type.GetMethod("AddConsumer", BindingFlags.Instance | BindingFlags.Public);
            var removeConsumer = type.GetMethod("RemoveConsumer", BindingFlags.Instance | BindingFlags.Public);

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
                stream.AddConsumer();
                stream.AddConsumer();

                Assert.Equal(2, stream.ConsumerCount);

                stream.RemoveConsumer();
                stream.RemoveConsumer();
                stream.RemoveConsumer();

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
                                stream.AddConsumer();
                            }

                            postAddBarrier.SignalAndWait();
                            removeBarrier.SignalAndWait();
                            for (var i = 0; i < operationsPerThread; i++)
                            {
                                stream.RemoveConsumer();
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
        public void AddConsumerAndRemoveConsumerRemainPublicMethodsUntilEmbyReferencesExposeRuntimeSlots()
        {
            var interfaceMethodNames = typeof(ILiveStream).GetMethods().Select(method => method.Name).ToArray();

            Assert.DoesNotContain("AddConsumer", interfaceMethodNames);
            Assert.DoesNotContain("RemoveConsumer", interfaceMethodNames);
            Assert.NotNull(typeof(XtreamLiveStream).GetMethod("AddConsumer"));
            Assert.NotNull(typeof(XtreamLiveStream).GetMethod("RemoveConsumer"));
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
    }
}
