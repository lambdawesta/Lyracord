using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Unicord.Universal.Models.Voice
{
    /// <summary>
    /// Short join/leave chimes for voice calls. The tones are synthesised here (Discord's own sounds are not
    /// redistributable) and played through the Communications category so they are heard in call mode too.
    /// </summary>
    internal static class VoiceSounds
    {
        public enum Kind { SelfJoined, SelfLeft, UserJoined, UserLeft }

        private const int SampleRate = 48000;
        private static readonly object _lock = new object();
        private static MediaPlayer _player;

        public static void Play(Kind kind)
        {
            if (!App.LocalSettings.Read("VoiceSounds", true))
                return;

            // Ascending notes for "joined", descending for "left"; the call's own events use three notes.
            double[] notes;
            switch (kind)
            {
                case Kind.SelfJoined: notes = new[] { 587.33, 739.99, 880.0 }; break;
                case Kind.SelfLeft: notes = new[] { 880.0, 739.99, 587.33 }; break;
                case Kind.UserJoined: notes = new[] { 659.25, 987.77 }; break;
                default: notes = new[] { 987.77, 659.25 }; break;
            }

            _ = Task.Run(() => PlayTones(notes));
        }

        private static void PlayTones(double[] notes)
        {
            try
            {
                var wav = CreateWav(notes, noteMs: 90, gapMs: 15, volume: 0.22);
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(wav);
                    writer.StoreAsync().AsTask().Wait();
                    writer.DetachStream();
                }

                lock (_lock)
                {
                    if (_player == null)
                    {
                        _player = new MediaPlayer
                        {
                            AudioCategory = MediaPlayerAudioCategory.Communications,
                            AutoPlay = true,
                        };
                    }

                    _player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private static byte[] CreateWav(double[] notes, int noteMs, int gapMs, double volume)
        {
            var noteSamples = SampleRate * noteMs / 1000;
            var gapSamples = SampleRate * gapMs / 1000;
            var fade = SampleRate * 8 / 1000; // 8 ms fade in/out, no clicks
            var total = notes.Length * (noteSamples + gapSamples);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + total * 2);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);          // PCM
                w.Write((short)1);          // mono
                w.Write(SampleRate);
                w.Write(SampleRate * 2);    // byte rate
                w.Write((short)2);          // block align
                w.Write((short)16);         // bits per sample
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(total * 2);

                foreach (var freq in notes)
                {
                    for (var i = 0; i < noteSamples; i++)
                    {
                        var envelope = Math.Min(1.0, Math.Min(i, noteSamples - 1 - i) / (double)fade);
                        // a little second harmonic makes it sound like a chime rather than a beep
                        var t = i / (double)SampleRate;
                        var sample = Math.Sin(2 * Math.PI * freq * t) * 0.8 + Math.Sin(4 * Math.PI * freq * t) * 0.2;
                        w.Write((short)(sample * envelope * volume * short.MaxValue));
                    }
                    for (var i = 0; i < gapSamples; i++)
                        w.Write((short)0);
                }

                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
