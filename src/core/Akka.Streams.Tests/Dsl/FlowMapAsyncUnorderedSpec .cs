﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Streams;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Streams.Dsl;
using Akka.Streams.Implementation;
using Akka.Streams.Supervision;
using Akka.Streams.TestKit;
using Akka.Streams.TestKit.Tests;
using Akka.TestKit;
using Akka.TestKit.Internal;
using Akka.Util.Internal;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable InvokeAsExtensionMethod
#pragma warning disable 162

namespace Akka.Streams.Tests.Dsl
{
    public class FlowMapAsyncUnorderedSpec : AkkaSpec
    {
        private ActorMaterializer Materializer { get; }

        public FlowMapAsyncUnorderedSpec(ITestOutputHelper helper) : base(helper)
        {
            Materializer = ActorMaterializer.Create(Sys);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_produce_future_elements_in_the_order_they_are_ready()
        {
            this.AssertAllStagesStopped(() =>
            {
                var c = TestSubscriber.CreateManualProbe<int>(this);
                var latch = Enumerable.Range(0, 4).Select(_ => new TestLatch(1)).ToArray();

                Source.From(Enumerable.Range(0, 4)).MapAsyncUnordered(4, n => Task.Run(() =>
                {
                    latch[n].Ready(TimeSpan.FromSeconds(5));
                    return n;
                })).To(Sink.FromSubscriber<int, Unit>(c)).Run(Materializer);
                var sub = c.ExpectSubscription();
                sub.Request(5);

                latch[1].CountDown();
                c.ExpectNext(1);

                latch[3].CountDown();
                c.ExpectNext(3);

                latch[2].CountDown();
                c.ExpectNext(2);

                latch[0].CountDown();
                c.ExpectNext(0);

                c.ExpectComplete();
            }, Materializer);
            
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_not_run_more_futures_than_requested_elements()
        {
            var probe = CreateTestProbe();
            var c = TestSubscriber.CreateManualProbe<int>(this);
            Source.From(Enumerable.Range(1, 20))
                .MapAsyncUnordered(4, n => Task.Run(() =>
                {
                    probe.Ref.Tell(n);
                    return n;
                }))
                .To(Sink.FromSubscriber<int, Unit>(c)).Run(Materializer);
            var sub = c.ExpectSubscription();
            c.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
            probe.ExpectNoMsg(TimeSpan.Zero);
            sub.Request(1);
            var got = new List<int> {c.ExpectNext()};
            probe.ExpectMsgAllOf(1, 2, 3, 4, 5);
            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(500));
            sub.Request(25);
            probe.ExpectMsgAllOf(Enumerable.Range(6, 15).ToArray());
            c.Within(TimeSpan.FromSeconds(3), () =>
            {
                Enumerable.Range(2, 19).ForEach(_ => got.Add(c.ExpectNext()));
                return Unit.Instance;
            });
            got.ShouldAllBeEquivalentTo(Enumerable.Range(1, 20));
            c.ExpectComplete();
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_signal_future_failure()
        {
            this.AssertAllStagesStopped(() =>
            {
                var latch = new TestLatch(1);
                var c = TestSubscriber.CreateManualProbe<int>(this);
                Source.From(Enumerable.Range(1, 5))
                    .MapAsyncUnordered(4, n => Task.Run(() =>
                    {
                        if (n == 3)
                            throw new TestException("err1");

                        latch.Ready(TimeSpan.FromSeconds(10));
                        return n;
                    }))
                    .To(Sink.FromSubscriber<int, Unit>(c)).Run(Materializer);
                var sub = c.ExpectSubscription();
                sub.Request(10);
                c.ExpectError().InnerException.Message.Should().Be("err1");
                latch.CountDown();
            }, Materializer);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_signal_error_from_MapAsyncUnordered()
        {
            this.AssertAllStagesStopped(() =>
            {
                var latch = new TestLatch(1);
                var c = TestSubscriber.CreateManualProbe<int>(this);
                Source.From(Enumerable.Range(1, 5))
                    .MapAsyncUnordered(4, n =>
                    {
                        if (n == 3)
                            throw new TestException("err2");

                        return Task.Run(() =>
                        {
                            latch.Ready(TimeSpan.FromSeconds(10));
                            return n;
                        });
                    })
                    .RunWith(Sink.FromSubscriber<int, Unit>(c), Materializer);
                var sub = c.ExpectSubscription();
                sub.Request(10);
                c.ExpectError().Message.Should().Be("err2");
                latch.CountDown();
            }, Materializer);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_resume_after_future_failure()
        {
            this.AssertAllStagesStopped(() =>
            {
                this.AssertAllStagesStopped(() =>
                {
                    Source.From(Enumerable.Range(1, 5))
                        .MapAsyncUnordered(4, n => Task.Run(() =>
                        {
                            if (n == 3)
                                throw new TestException("err3");
                            return n;
                        }))
                        .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Deciders.ResumingDecider))
                        .RunWith(this.SinkProbe<int>(), Materializer)
                        .Request(10)
                        .ExpectNextUnordered(1, 2, 4, 5)
                        .ExpectComplete();
                }, Materializer);
            }, Materializer);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_resume_after_multiple_failures()
        {
            this.AssertAllStagesStopped(() =>
            {
                var futures = new[]
                {
                    Task.Run(() => { throw new TestException("failure1"); return "";}),
                    Task.Run(() => { throw new TestException("failure2"); return "";}),
                    Task.Run(() => { throw new TestException("failure3"); return "";}),
                    Task.Run(() => { throw new TestException("failure4"); return "";}),
                    Task.Run(() => { throw new TestException("failure5"); return "";}),
                    Task.FromResult("happy")
                };

                var t = Source.From(futures)
                    .MapAsyncUnordered(2, x => x)
                    .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Deciders.ResumingDecider))
                    .RunWith(Sink.First<string>(), Materializer);

                t.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
                t.Result.Should().Be("happy");
            }, Materializer);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_finish_after_future_failure()
        {
            this.AssertAllStagesStopped(() =>
            {
                var t = Source.From(Enumerable.Range(1, 3))
                    .MapAsyncUnordered(1, n => Task.Run(() =>
                    {
                        if (n == 3)
                            throw new TestException("err3b");
                        return n;
                    }))
                    .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Deciders.ResumingDecider))
                    .Grouped(10)
                    .RunWith(Sink.First<IEnumerable<int>>(), Materializer);

                t.Wait(TimeSpan.FromSeconds(1)).Should().BeTrue();
                t.Result.ShouldAllBeEquivalentTo(new[] {1, 2});
            }, Materializer);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_resume_when_MapAsyncUnordered_throws()
        {
            Source.From(Enumerable.Range(1, 5))
                .MapAsyncUnordered(4, n =>
                {
                    if (n == 3)
                        throw new TestException("err4");
                    return Task.FromResult(n);
                })
                .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Deciders.ResumingDecider))
                .RunWith(this.SinkProbe<int>(), Materializer)
                .Request(10)
                .ExpectNextUnordered(1, 2, 4, 5)
                .ExpectComplete();
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_signal_NPE_when_future_is_completed_with_null()
        {
            var expected = ReactiveStreamsCompliance.ElementMustNotBeNullMsg + Environment.NewLine + "Parametername: element";
            var c = TestSubscriber.CreateManualProbe<string>(this);

            Source.From(new[] {"a", "b"})
                .MapAsyncUnordered(4, _ => Task.FromResult(null as string))
                .To(Sink.FromSubscriber<string, Unit>(c)).Run(Materializer);

            var sub = c.ExpectSubscription();
            sub.Request(10);
            c.ExpectError().Message.Should().Be(expected);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_resume_when_future_is_completed_with_null()
        {
            var c = TestSubscriber.CreateManualProbe<string>(this);
            Source.From(new[] { "a", "b", "c" })
                .MapAsyncUnordered(4, s => s.Equals("b") ? Task.FromResult(null as string) : Task.FromResult(s))
                .WithAttributes(ActorAttributes.CreateSupervisionStrategy(Deciders.ResumingDecider))
                .To(Sink.FromSubscriber<string, Unit>(c)).Run(Materializer);
            var sub = c.ExpectSubscription();
            sub.Request(10);
            c.ExpectNextUnordered("a", "c");
            c.ExpectComplete();
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_handle_cancel_properly()
        {
            this.AssertAllStagesStopped(() =>
            {
                var pub = TestPublisher.CreateManualProbe<int>(this);
                var sub = TestSubscriber.CreateManualProbe<int>(this);

                Source.FromPublisher<int, Unit>(pub)
                    .MapAsyncUnordered(4, _ => Task.FromResult(0))
                    .RunWith(Sink.FromSubscriber<int, Unit>(sub), Materializer);

                var upstream = pub.ExpectSubscription();
                upstream.ExpectRequest();

                sub.ExpectSubscription().Cancel();

                upstream.ExpectCancellation();
            }, Materializer);
        }

        [Fact]
        public void A_Flow_with_MapAsyncUnordered_must_not_run_more_futures_than_configured()
        {
            this.AssertAllStagesStopped(() =>
            {
                const int parallelism = 8;
                var counter = new AtomicCounter();
                var queue = new BlockingQueue<Tuple<TaskCompletionSource<int>, long>>();

                var timer = new Thread(() =>
                {
                    var delay = 50000; // nanoseconds
                    var count = 0;
                    var cont = true;
                    while (cont)
                    {
                        try
                        {
                            var t = queue.Take(CancellationToken.None);
                            var promise = t.Item1;
                            var enqueued = t.Item2;
                            var wakeup = enqueued + delay;
                            while (DateTime.Now.Ticks < wakeup) { }
                            counter.Decrement();
                            promise.SetResult(count);
                            count++;
                        }
                        catch
                        {
                            cont = false;
                        }
                    }
                });

                timer.Start();

                Func<Task<int>> deferred = () =>
                {
                    if (counter.IncrementAndGet() > parallelism)
                        return Task.Run(() =>
                        {
                            throw new Exception("parallelism exceeded");
                            return 0;
                        });

                    var p = new TaskCompletionSource<int>();
                    queue.Enqueue(Tuple.Create(p, DateTime.Now.Ticks));
                    return p.Task;
                };

                try
                {
                    const int n = 10000;
                    var task = Source.From(Enumerable.Range(1, n))
                        .MapAsyncUnordered(parallelism, _ => deferred())
                        .RunFold(0, (c, _) => c + 1, Materializer);

                    task.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    task.Result.Should().Be(n);
                }
                finally
                {
                    timer.Interrupt();
                }
            }, Materializer);
        }
    }
}