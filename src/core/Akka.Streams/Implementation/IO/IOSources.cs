﻿using System;
using System.IO;
using System.Linq;
using System.Reactive.Streams;
using System.Threading.Tasks;
using Akka.IO;
using Akka.Streams.Actors;
using Akka.Streams.Implementation.Stages;
using Akka.Streams.IO;

namespace Akka.Streams.Implementation.IO
{
    /// <summary>
    /// INTERNAL API
    /// Creates simple synchronous Source backed by the given file.
    /// </summary>
    internal sealed class FileSource : SourceModule<ByteString, Task<IOResult>>
    {
        private readonly FileInfo _f;
        private readonly int _chunkSize;
        private readonly Attributes _attributes;

        public FileSource(FileInfo f, int chunkSize, Attributes attributes, SourceShape<ByteString> shape) : base(shape)
        {
            if(chunkSize <= 0)
                throw new ArgumentException("chunkSize must be greater than 0");

            _f = f;
            _chunkSize = chunkSize;
            _attributes = attributes;
        }

        public override Attributes Attributes => _attributes;

        public override IModule WithAttributes(Attributes attributes)
            => new FileSource(_f, _chunkSize, attributes, AmendShape(attributes));

        protected override SourceModule<ByteString, Task<IOResult>> NewInstance(SourceShape<ByteString> shape)
            => new FileSource(_f, _chunkSize, Attributes, shape);

        public override IPublisher<ByteString> Create(MaterializationContext context, out Task<IOResult> task)
        {
            // FIXME rewrite to be based on GraphStage rather than dangerous downcasts
            var materializer = ActorMaterializer.Downcast(context.Materializer);
            var settings = materializer.EffectiveSettings(context.EffectiveAttributes);

            var ioResultPromise = new TaskCompletionSource<IOResult>();
            var props = FilePublisher.Props(_f, ioResultPromise, _chunkSize, settings.InitialInputBufferSize, settings.MaxInputBufferSize);
            var dispatcher = context.EffectiveAttributes.GetAttribute(DefaultAttributes.IODispatcher.AttributeList.First()) as ActorAttributes.Dispatcher;
            var actorRef = materializer.ActorOf(context, props.WithDispatcher(dispatcher.Name));

            task = ioResultPromise.Task;
            return new ActorPublisherImpl<ByteString>(actorRef);

        }
    }

    /// <summary>
    /// INTERNAL API
    /// Source backed by the given input stream.
    /// </summary>
    internal sealed class InputStreamSource : SourceModule<ByteString, Task<IOResult>>
    {
        private readonly Func<Stream> _createInputStream;
        private readonly int _chunkSize;
        private readonly Attributes _attributes;

        public InputStreamSource(Func<Stream> createInputStream, int chunkSize, Attributes attributes, SourceShape<ByteString> shape) : base(shape)
        {
            _createInputStream = createInputStream;
            _chunkSize = chunkSize;
            _attributes = attributes;
        }

        public override Attributes Attributes => _attributes;

        public override IModule WithAttributes(Attributes attributes)
            => new InputStreamSource(_createInputStream, _chunkSize, attributes, AmendShape(attributes));

        protected override SourceModule<ByteString, Task<IOResult>> NewInstance(SourceShape<ByteString> shape)
            => new InputStreamSource(_createInputStream, _chunkSize, Attributes, shape);

        public override IPublisher<ByteString> Create(MaterializationContext context, out Task<IOResult> task)
        {
            var materializer = ActorMaterializer.Downcast(context.Materializer);
            var ioResultPromise = new TaskCompletionSource<IOResult>();
            IPublisher<ByteString> pub;
            
            try
            {
                // can throw, i.e. FileNotFound
                var inputStream = _createInputStream();
                var props = InputStreamPublisher.Props(inputStream, ioResultPromise, _chunkSize);
                var actorRef = materializer.ActorOf(context, props);
                pub = new ActorPublisherImpl<ByteString>(actorRef);
            }
            catch (Exception ex)
            {
                ioResultPromise.TrySetException(ex);
                pub = new ErrorPublisher<ByteString>(ex, Attributes.GetNameOrDefault("inputStreamSource"));
            }

            task = ioResultPromise.Task;
            return pub;
        }
    }
}
