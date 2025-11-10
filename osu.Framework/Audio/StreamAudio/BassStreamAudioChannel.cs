// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ManagedBass;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Logging;

namespace osu.Framework.Audio.StreamAudio
{
    /// <summary>
    /// Supported PCM formats for <see cref="BassStreamAudioChannel"/> submissions.
    /// </summary>
    public enum StreamAudioSampleFormat
    {
        /// <summary>
        /// 32-bit floating point samples.
        /// </summary>
        Float32,

        /// <summary>
        /// Signed 16-bit PCM samples.
        /// </summary>
        Pcm16,
    }

    /// <summary>
    /// <see cref="IBassAudioChannel"/> implementation backed by a push-based BASS stream, intended for real-time sources such as voice chat.
    /// </summary>
    public class BassStreamAudioChannel : AdjustableAudioComponent, IBassAudioChannel, IBassAudio
    {
        private const int bass_nodevice = 0x20000;

        private readonly ArrayPool<byte> bufferPool = ArrayPool<byte>.Shared;
        private readonly ConcurrentQueue<BufferChunk> pendingChunks = new ConcurrentQueue<BufferChunk>();
        private readonly BassRelativeFrequencyHandler frequencyHandler;

        private BufferChunk? activeChunk;
        private BassAmplitudeProcessor? amplitudeProcessor;

        private int handle;
        private bool userRequestedPlay;
        private bool pendingPlaybackStart;
        private bool endOfStreamQueued;
        private bool endOfStreamSent;

        private volatile bool playing;

        /// <summary>
        /// Creates a new <see cref="BassStreamAudioChannel"/>.
        /// </summary>
        /// <param name="name">An identifier for this stream.</param>
        /// <param name="sampleRate">The sample rate of the incoming PCM data.</param>
        /// <param name="channels">The number of PCM channels.</param>
        /// <param name="format">The PCM encoding used for submissions.</param>
        public BassStreamAudioChannel(string name, int sampleRate, int channels, StreamAudioSampleFormat format = StreamAudioSampleFormat.Float32)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Stream name must be non-empty", nameof(name));

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            if (channels <= 0 || channels > 8)
                throw new ArgumentOutOfRangeException(nameof(channels));

            Name = name;
            SampleRate = sampleRate;
            Channels = channels;
            SampleFormat = format;

            FrameStride = channels * BytesPerSample;

            frequencyHandler = new BassRelativeFrequencyHandler
            {
                FrequencyChangedToZero = stopInternal,
                FrequencyChangedFromZero = () =>
                {
                    if (userRequestedPlay)
                        startInternal();
                }
            };

            EnqueueAction(createStream);
        }

        /// <summary>
        /// Human-readable identifier for this channel.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Sample rate of the PCM data (Hz).
        /// </summary>
        public int SampleRate { get; }

        /// <summary>
        /// Number of PCM channels (1 = mono, 2 = stereo, etc.).
        /// </summary>
        public int Channels { get; }

        /// <summary>
        /// Encoding of PCM frames accepted by this channel.
        /// </summary>
        public StreamAudioSampleFormat SampleFormat { get; }

        /// <summary>
        /// Number of bytes per PCM sample.
        /// </summary>
        public int BytesPerSample => SampleFormat == StreamAudioSampleFormat.Float32 ? sizeof(float) : sizeof(short);

        /// <summary>
        /// Number of bytes per PCM frame (sample * channel count).
        /// </summary>
        public int FrameStride { get; }

        /// <summary>
        /// Indicates whether playback was requested. Actual audible playback may lag until the stream is added to a mixer.
        /// </summary>
        public bool Playing => playing || pendingPlaybackStart;

        /// <summary>
        /// Adds PCM data to the internal queue.
        /// </summary>
        /// <remarks>
        /// The data must be aligned to complete frames (sample * channel count).
        /// </remarks>
        /// <param name="data">PCM bytes in the configured <see cref="SampleFormat"/>.</param>
        public void Submit(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (data.Length == 0)
                return;

            validateStride(data.Length);

            byte[] buffer = bufferPool.Rent(data.Length);
            data.CopyTo(buffer);
            pendingChunks.Enqueue(new BufferChunk(buffer, data.Length));
        }

        /// <summary>
        /// Adds PCM data encoded as 16-bit signed integers.
        /// </summary>
        public void Submit(ReadOnlySpan<short> samples)
        {
            if (SampleFormat != StreamAudioSampleFormat.Pcm16)
                throw new InvalidOperationException($"This channel is configured for {SampleFormat} data.");

            Submit(MemoryMarshal.AsBytes(samples));
        }

        /// <summary>
        /// Adds PCM data encoded as 32-bit floats.
        /// </summary>
        public void Submit(ReadOnlySpan<float> samples)
        {
            if (SampleFormat != StreamAudioSampleFormat.Float32)
                throw new InvalidOperationException($"This channel is configured for {SampleFormat} data.");

            Submit(MemoryMarshal.AsBytes(samples));
        }

        /// <summary>
        /// Signals that no further data will be queued.
        /// </summary>
        public void Complete()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (endOfStreamQueued)
                return;

            endOfStreamQueued = true;
            pendingChunks.Enqueue(BufferChunk.End());
        }

        /// <summary>
        /// Starts playback via the assigned mixer.
        /// </summary>
        public void Play()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            userRequestedPlay = true;
            pendingPlaybackStart = true;

            EnqueueAction(() =>
            {
                if (startInternal())
                    playing = true;

                pendingPlaybackStart = false;
            });
        }

        /// <summary>
        /// Pauses playback without clearing queued data.
        /// </summary>
        public void Stop()
        {
            userRequestedPlay = false;

            EnqueueAction(() =>
            {
                stopInternal();
                playing = false;
            });
        }

        /// <summary>
        /// Clears queued but unconsumed audio data.
        /// </summary>
        public void ClearQueuedData()
        {
            while (pendingChunks.TryDequeue(out var chunk))
                chunk.Return(bufferPool);

            activeChunk?.Return(bufferPool);
            activeChunk = null;
        }

        protected override void UpdateState()
        {
            base.UpdateState();

            if (handle == 0 || Mixer == null)
                return;

            pushQueuedAudio();
            updatePlaybackState();
            amplitudeProcessor?.Update();
        }

        private void validateStride(int byteCount)
        {
            if (byteCount % FrameStride != 0)
                throw new ArgumentException($"Data length must be a multiple of frame size ({FrameStride} bytes).", nameof(byteCount));
        }

        private void createStream()
        {
            if (handle != 0)
                return;

            BassFlags flags = BassFlags.Decode;

            if (SampleFormat == StreamAudioSampleFormat.Float32)
                flags |= BassFlags.Float;

            handle = Bass.CreateStream(SampleRate, Channels, flags, StreamProcedureType.Push);
            // handle = Bass.CreateStream("https://i.ppy.sh/c3be0096b77372d7a1df878e90b2e88eac0c4b3d/68747470733a2f2f66696c65732e636174626f782e6d6f652f6561616a39392e6d3461", 0, BassFlags.Loop | BassFlags.Decode,
            //     (a, b, c) =>
            //     {
            //     });
            Bass.ChannelSetDevice(handle, bass_nodevice);
            Bass.ChannelSetAttribute(handle, ChannelAttribute.Buffer, 0.05f);

            if (handle == 0)
                return;

            frequencyHandler.SetChannel(handle);
            OnStateChanged();
        }

        private bool startInternal()
        {
            if (handle == 0)
                createStream();

            if (handle == 0 || Mixer == null)
                return false;

            InvalidateState();

            if (frequencyHandler.IsFrequencyZero)
                return true;

            return bassMixer.ChannelPlay(this);
            //return Bass.ChannelPlay(handle, true);
        }

        private void stopInternal()
        {
            if (handle == 0 || Mixer == null)
                return;

            bassMixer.ChannelPause(this);
        }

        private void pushQueuedAudio()
        {
            while (true)
            {
                var chunk = activeChunk ?? dequeueChunk();
                if (chunk == null)
                    break;

                if (chunk.IsEnd)
                {
                    sendEndOfStream();
                    chunk.Return(bufferPool);
                    activeChunk = null;
                    continue;
                }

                int written = Bass.StreamPutData(handle, chunk.Buffer!, chunk.Length);

                if (written == -1)
                {
                    BassUtils.CheckFaulted(false);
                    activeChunk = chunk;
                    break;
                }

                if (written == 0)
                {
                    activeChunk = chunk;
                    break;
                }

                if (written < chunk.Length)
                {
                    Buffer.BlockCopy(chunk.Buffer!, written, chunk.Buffer!, 0, chunk.Length - written);
                    chunk.Length -= written;
                    activeChunk = chunk;
                    break;
                }

                chunk.Return(bufferPool);
                activeChunk = null;
            }
        }

        private BufferChunk? dequeueChunk()
        {
            return pendingChunks.TryDequeue(out var chunk) ? chunk : null;
        }

        private void sendEndOfStream()
        {
            if (endOfStreamSent)
                return;

            endOfStreamSent = true;
            Bass.StreamPutData(handle, IntPtr.Zero, (int)StreamProcedureType.End);
        }

        private void updatePlaybackState()
        {
            if (Mixer == null)
                return;

            switch (bassMixer.ChannelIsActive(this))
            {
                case PlaybackState.Playing:
                case PlaybackState.Stalled:
                case PlaybackState.Paused when userRequestedPlay:
                    playing = true;
                    break;

                default:
                    playing = false;
                    break;
            }
        }

        internal override void OnStateChanged()
        {
            base.OnStateChanged();

            if (handle == 0)
                return;

            Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, AggregateVolume.Value);
            Bass.ChannelSetAttribute(handle, ChannelAttribute.Pan, AggregateBalance.Value);
            frequencyHandler.SetFrequency(AggregateFrequency.Value);
        }

        public ChannelAmplitudes CurrentAmplitudes => (amplitudeProcessor ??= new BassAmplitudeProcessor(this)).CurrentAmplitudes;

        #region IBassAudioChannel

        bool IBassAudioChannel.IsActive => !IsDisposed;

        int IBassAudioChannel.Handle => handle;

        bool IBassAudioChannel.MixerChannelPaused { get; set; } = true;

        BassAudioMixer IBassAudioChannel.Mixer => bassMixer;

        private BassAudioMixer bassMixer => (BassAudioMixer)Mixer.AsNonNull();

        #endregion

        #region IAudioChannel

        protected virtual AudioMixer? Mixer { get; set; }

        AudioMixer? IAudioChannel.Mixer
        {
            get => Mixer;
            set => Mixer = value;
        }

        System.Threading.Tasks.Task IAudioChannel.EnqueueAction(Action action) => EnqueueAction(action);

        #endregion

        void IBassAudio.UpdateDevice(int deviceIndex)
        {
            EnqueueAction(createStream);
        }

        protected override void Dispose(bool disposing)
        {
            ClearQueuedData();

            if (handle != 0)
            {
                if (Mixer is BassAudioMixer bass)
                    bass.StreamFree(this);
                else
                    Bass.StreamFree(handle);

                handle = 0;
            }

            base.Dispose(disposing);
        }

        private sealed class BufferChunk
        {
            public BufferChunk(byte[]? buffer, int length, bool isEnd = false)
            {
                Buffer = buffer;
                Length = length;
                IsEnd = isEnd;
            }

            public byte[]? Buffer { get; }
            public int Length { get; set; }
            public bool IsEnd { get; }

            public static BufferChunk End() => new BufferChunk(null, 0, true);

            public void Return(ArrayPool<byte> pool)
            {
                if (Buffer != null)
                    pool.Return(Buffer);
            }
        }
    }
}
