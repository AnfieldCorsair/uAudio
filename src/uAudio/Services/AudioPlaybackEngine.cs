using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using uAudioEditorPro.Models;

namespace uAudioEditorPro.Services
{
    public sealed class TrackSampleProvider : ISampleProvider, IDisposable
    {
        private const double TimeEpsilon = 1e-7;

        private readonly object _syncRoot = new object();
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly WaveFormat _waveFormat;
        private readonly Dictionary<string, FilePlaybackContext> _fileContexts;

        private List<AudioClipModel> _clips;
        private double _currentGlobalTime;
        private float _volume;
        private bool _isMuted;
        private bool _disposed;

        private Guid _continuationClipId;
        private string? _continuationFilePath;
        private double _expectedFileTime;
        private bool _hasContinuation;

        private sealed class FilePlaybackContext : IDisposable
        {
            private readonly int _targetSampleRate;
            private readonly int _targetChannels;

            public AudioFileReader Reader { get; }
            public ISampleProvider Provider { get; private set; }

            public FilePlaybackContext(string filePath, int targetSampleRate, int targetChannels)
            {
                _targetSampleRate = targetSampleRate;
                _targetChannels = targetChannels;
                Reader = new AudioFileReader(filePath);
                Provider = BuildProvider();
            }

            public void Seek(double seconds)
            {
                double clampedSeconds = Math.Max(0d, Math.Min(seconds, Reader.TotalTime.TotalSeconds));
                Reader.CurrentTime = TimeSpan.FromSeconds(clampedSeconds);
                Provider = BuildProvider();
            }

            private ISampleProvider BuildProvider()
            {
                ISampleProvider provider = Reader;

                if (provider.WaveFormat.Channels != _targetChannels)
                {
                    provider = CreateChannelMappedProvider(provider, _targetChannels);
                }

                if (provider.WaveFormat.SampleRate != _targetSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, _targetSampleRate);
                }

                return provider;
            }

            public void Dispose()
            {
                Reader.Dispose();
            }
        }

        public TrackSampleProvider(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            _fileContexts = new Dictionary<string, FilePlaybackContext>(StringComparer.OrdinalIgnoreCase);
            _clips = new List<AudioClipModel>();
            _volume = 1.0f;
            _continuationClipId = Guid.Empty;
        }

        public WaveFormat WaveFormat => _waveFormat;

        public double CurrentGlobalTime
        {
            get
            {
                lock (_syncRoot)
                {
                    return _currentGlobalTime;
                }
            }
        }

        public float Volume
        {
            get
            {
                lock (_syncRoot)
                {
                    return _volume;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _volume = Math.Clamp(value, 0f, 2f);
                }
            }
        }

        public bool IsMuted
        {
            get
            {
                lock (_syncRoot)
                {
                    return _isMuted;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    _isMuted = value;
                }
            }
        }

        public void UpdateClips(List<AudioClipModel> clips)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                _clips = (clips ?? new List<AudioClipModel>())
                    .Select(c => c.Clone())
                    .OrderBy(c => c.StartTime)
                    .ThenBy(c => c.Id)
                    .ToList();

                _hasContinuation = false;
                CleanupUnusedContexts();
            }
        }

        public void Seek(double globalTime)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                _currentGlobalTime = Math.Max(0d, globalTime);
                _hasContinuation = false;
                _continuationClipId = Guid.Empty;
                _continuationFilePath = null;
                _expectedFileTime = 0d;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();

                Array.Clear(buffer, offset, count);

                int samplesWritten = 0;

                while (samplesWritten < count)
                {
                    AudioClipModel? activeClip = FindClipAtTime(_currentGlobalTime);

                    if (activeClip == null)
                    {
                        int silenceSamples = CalculateSilenceSamples(count - samplesWritten, _currentGlobalTime);
                        samplesWritten += silenceSamples;
                        _currentGlobalTime += SamplesToSeconds(silenceSamples);
                        _hasContinuation = false;
                        continue;
                    }

                    double localTime = Math.Max(0d, _currentGlobalTime - activeClip.StartTime);
                    double remainingClipTime = activeClip.Duration - localTime;

                    if (remainingClipTime <= TimeEpsilon)
                    {
                        _currentGlobalTime = activeClip.EndTime;
                        _hasContinuation = false;
                        continue;
                    }

                    int clipSamplesRemaining = SecondsToSamples(remainingClipTime);
                    if (clipSamplesRemaining <= 0)
                    {
                        clipSamplesRemaining = Math.Min(count - samplesWritten, _channels);
                    }

                    int blockSamples = Math.Min(count - samplesWritten, clipSamplesRemaining);
                    double blockDuration = SamplesToSeconds(blockSamples);

                    if (_isMuted || _volume <= 0f)
                    {
                        samplesWritten += blockSamples;
                        _currentGlobalTime += blockDuration;
                        _hasContinuation = false;
                        continue;
                    }

                    FilePlaybackContext? context = GetOrCreateContext(activeClip.FilePath);
                    if (context == null)
                    {
                        samplesWritten += blockSamples;
                        _currentGlobalTime += blockDuration;
                        _hasContinuation = false;
                        continue;
                    }

                    double targetFileTime = Math.Max(0d, activeClip.OffsetInFile + localTime);
                    EnsureContextPosition(context, activeClip, targetFileTime);

                    int actualRead = context.Provider.Read(buffer, offset + samplesWritten, blockSamples);

                    if (actualRead < blockSamples)
                    {
                        Array.Clear(buffer, offset + samplesWritten + actualRead, blockSamples - actualRead);
                    }

                    if (actualRead > 0 && Math.Abs(_volume - 1f) > 0.0001f)
                    {
                        for (int i = 0; i < actualRead; i++)
                        {
                            buffer[offset + samplesWritten + i] *= _volume;
                        }
                    }

                    samplesWritten += blockSamples;
                    _currentGlobalTime += blockDuration;

                    if (actualRead == blockSamples)
                    {
                        _continuationClipId = activeClip.Id;
                        _continuationFilePath = activeClip.FilePath;
                        _expectedFileTime = targetFileTime + blockDuration;
                        _hasContinuation = true;
                    }
                    else
                    {
                        _hasContinuation = false;
                    }
                }

                return count;
            }
        }

        private void EnsureContextPosition(FilePlaybackContext context, AudioClipModel clip, double targetFileTime)
        {
            bool needReset =
                !_hasContinuation ||
                _continuationClipId != clip.Id ||
                !string.Equals(_continuationFilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(_expectedFileTime - targetFileTime) > (1.5d / _sampleRate);

            if (needReset)
            {
                context.Seek(targetFileTime);
            }
        }

        private AudioClipModel? FindClipAtTime(double globalTime)
        {
            for (int i = 0; i < _clips.Count; i++)
            {
                AudioClipModel clip = _clips[i];
                if (globalTime + TimeEpsilon >= clip.StartTime && globalTime < clip.EndTime - TimeEpsilon)
                {
                    return clip;
                }
            }

            return null;
        }

        private AudioClipModel? FindNextClipAfter(double globalTime)
        {
            for (int i = 0; i < _clips.Count; i++)
            {
                AudioClipModel clip = _clips[i];
                if (clip.StartTime > globalTime + TimeEpsilon)
                {
                    return clip;
                }
            }

            return null;
        }

        private int CalculateSilenceSamples(int maxSamples, double currentTime)
        {
            AudioClipModel? nextClip = FindNextClipAfter(currentTime);
            if (nextClip == null)
            {
                return maxSamples;
            }

            double silenceDuration = nextClip.StartTime - currentTime;
            if (silenceDuration <= TimeEpsilon)
            {
                return Math.Min(maxSamples, Math.Max(_channels, 1));
            }

            int silenceSamples = SecondsToSamples(silenceDuration);
            if (silenceSamples <= 0)
            {
                silenceSamples = Math.Max(_channels, 1);
            }

            return Math.Min(maxSamples, silenceSamples);
        }

        private int SecondsToSamples(double seconds)
        {
            if (seconds <= 0d)
            {
                return 0;
            }

            long frames = (long)Math.Ceiling((seconds * _sampleRate) - TimeEpsilon);
            if (frames < 1)
            {
                frames = 1;
            }

            long totalSamples = frames * _channels;
            if (totalSamples > int.MaxValue)
            {
                totalSamples = int.MaxValue - (int.MaxValue % _channels);
            }

            return (int)totalSamples;
        }

        private double SamplesToSeconds(int samples)
        {
            if (samples <= 0)
            {
                return 0d;
            }

            return (double)samples / (_sampleRate * _channels);
        }

        private FilePlaybackContext? GetOrCreateContext(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            if (_fileContexts.TryGetValue(filePath, out FilePlaybackContext? existing))
            {
                return existing;
            }

            try
            {
                FilePlaybackContext context = new FilePlaybackContext(filePath, _sampleRate, _channels);
                _fileContexts[filePath] = context;
                return context;
            }
            catch
            {
                return null;
            }
        }

        private void CleanupUnusedContexts()
        {
            HashSet<string> usedFiles = new HashSet<string>(_clips.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);
            List<string> keysToRemove = _fileContexts.Keys.Where(k => !usedFiles.Contains(k)).ToList();

            foreach (string key in keysToRemove)
            {
                if (_fileContexts.TryGetValue(key, out FilePlaybackContext? context))
                {
                    context.Dispose();
                    _fileContexts.Remove(key);
                }
            }
        }

        private static ISampleProvider CreateChannelMappedProvider(ISampleProvider source, int outputChannels)
        {
            if (source.WaveFormat.Channels == outputChannels)
            {
                return source;
            }

            if (source.WaveFormat.Channels == 2 && outputChannels == 1)
            {
                StereoToMonoSampleProvider mono = new StereoToMonoSampleProvider(source)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
                return mono;
            }

            MultiplexingSampleProvider mux = new MultiplexingSampleProvider(new[] { source }, outputChannels);
            int inputChannels = source.WaveFormat.Channels;

            for (int outputChannel = 0; outputChannel < outputChannels; outputChannel++)
            {
                int inputChannel = inputChannels == 1 ? 0 : outputChannel % inputChannels;
                mux.ConnectInputToOutput(inputChannel, outputChannel);
            }

            return mux;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TrackSampleProvider));
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (FilePlaybackContext context in _fileContexts.Values)
                {
                    context.Dispose();
                }

                _fileContexts.Clear();
                _disposed = true;
            }
        }
    }

    public sealed class AudioPlaybackEngine : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly List<TrackSampleProvider> _trackProviders;
        private readonly int _sampleRate;
        private readonly int _channels;

        private WaveOutEvent? _waveOut;
        private MixingSampleProvider? _mixer;
        private double _globalTime;
        private bool _disposed;

        public AudioPlaybackEngine(int sampleRate = 44100, int channels = 2)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _trackProviders = new List<TrackSampleProvider>();
        }

        public bool IsPlaying
        {
            get
            {
                lock (_syncRoot)
                {
                    return _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;
                }
            }
        }

        public double GlobalTime
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_trackProviders.Count > 0)
                    {
                        return _trackProviders[0].CurrentGlobalTime;
                    }

                    return _globalTime;
                }
            }
        }

        public TrackSampleProvider AddTrack()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                double currentTime = GetCurrentTimeLocked();
                TrackSampleProvider provider = new TrackSampleProvider(_sampleRate, _channels);
                provider.Seek(currentTime);

                _trackProviders.Add(provider);
                RebuildMixerLocked(currentTime);

                return provider;
            }
        }

        public void RemoveTrack(TrackSampleProvider provider)
        {
            if (provider == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                double currentTime = GetCurrentTimeLocked();

                if (_trackProviders.Remove(provider))
                {
                    provider.Dispose();
                    _globalTime = currentTime;
                    RebuildMixerLocked(currentTime);
                }
            }
        }

        public List<TrackSampleProvider> GetAllProviders()
        {
            lock (_syncRoot)
            {
                return new List<TrackSampleProvider>(_trackProviders);
            }
        }

        public void Play()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                double currentTime = GetCurrentTimeLocked();

                if (_mixer == null)
                {
                    CreateMixerLocked(currentTime);
                }

                EnsureWaveOutLocked();

                if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing)
                {
                    _waveOut.Play();
                }
            }
        }

        public void Pause()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _waveOut?.Pause();
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _waveOut?.Stop();
                SeekAllLocked(0d);
            }
        }

        public void Seek(double time)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                SeekAllLocked(Math.Max(0d, time));
            }
        }

        private double GetCurrentTimeLocked()
        {
            if (_trackProviders.Count > 0)
            {
                return _trackProviders[0].CurrentGlobalTime;
            }

            return _globalTime;
        }

        private void SeekAllLocked(double time)
        {
            _globalTime = time;

            for (int i = 0; i < _trackProviders.Count; i++)
            {
                _trackProviders[i].Seek(time);
            }
        }

        private void RebuildMixerLocked(double currentTime)
        {
            bool resumePlayback = _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;

            DisposeWaveOutLocked();
            CreateMixerLocked(currentTime);

            if (resumePlayback)
            {
                EnsureWaveOutLocked();
                _waveOut?.Play();
            }
        }

        private void CreateMixerLocked(double currentTime)
        {
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            _mixer = new MixingSampleProvider(format)
            {
                ReadFully = true
            };

            for (int i = 0; i < _trackProviders.Count; i++)
            {
                _trackProviders[i].Seek(currentTime);
                _mixer.AddMixerInput(_trackProviders[i]);
            }

            _globalTime = currentTime;
        }

        private void EnsureWaveOutLocked()
        {
            if (_waveOut != null || _mixer == null)
            {
                return;
            }

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 120,
                NumberOfBuffers = 3
            };

            _waveOut.Init(_mixer);
        }

        private void DisposeWaveOutLocked()
        {
            if (_waveOut == null)
            {
                return;
            }

            try
            {
                _waveOut.Stop();
            }
            catch
            {
            }

            try
            {
                _waveOut.Dispose();
            }
            catch
            {
            }

            _waveOut = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AudioPlaybackEngine));
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                DisposeWaveOutLocked();

                for (int i = 0; i < _trackProviders.Count; i++)
                {
                    _trackProviders[i].Dispose();
                }

                _trackProviders.Clear();
                _disposed = true;
            }
        }
    }
}