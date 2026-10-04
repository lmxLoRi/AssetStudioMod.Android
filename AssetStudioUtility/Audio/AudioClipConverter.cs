using System;
using System.Runtime.InteropServices;
using FMOD;
using WavHelper;

namespace AssetStudio
{
    public sealed class AudioClipConverter
    {
        private AudioClip m_AudioClip;
        private static FMOD.System system;
        public bool IsSupport => m_AudioClip.IsConvertSupport();
        public bool IsLegacy => m_AudioClip.IsLegacyConvertSupport();

        private static bool systemReady;
        private static string systemError;

        /// <summary>
        /// Brings FMOD up on first use rather than in a static constructor.
        ///
        /// RawAudioClipToWav needs no FMOD at all -- it writes a 44 byte header and copies the bytes
        /// -- but it touches this type, so the static constructor used to initialise FMOD anyway and
        /// threw before reaching it. On a platform without the native library that made the whole
        /// class unusable, including the parts that never needed it. The failure is also remembered:
        /// a missing library is not going to appear between two clips.
        /// </summary>
        private static bool EnsureSystem()
        {
            if (systemReady) return true;
            if (systemError != null) return false;

            try
            {
                var result = Factory.System_Create(out system);
                if (result != RESULT.OK)
                {
                    systemError = $"FMOD error! {result} - {Error.String(result)}";
                    return false;
                }
                result = system.init(1, INITFLAGS.NORMAL, IntPtr.Zero);
                if (result != RESULT.OK)
                {
                    systemError = $"FMOD error! {result} - {Error.String(result)}";
                    return false;
                }
                systemReady = true;
                return true;
            }
            catch (Exception e)
            {
                // DllNotFoundException or a type initialisation failure from the binding itself.
                systemError = $"no usable FMOD native library ({e.GetType().Name})";
                return false;
            }
        }

        /// <summary>Null when FMOD is usable, otherwise why it is not.</summary>
        public static string UnavailableReason => systemReady || systemError == null ? null : systemError;

        public AudioClipConverter(AudioClip audioClip)
        {
            m_AudioClip = audioClip;
        }

        public byte[] ConvertToWav(byte[] m_AudioData, ref string debugLog)
        {
            if (!EnsureSystem())
            {
                debugLog += systemError + "\n";
                return null;
            }

            var exinfo = new CREATESOUNDEXINFO();
            exinfo.cbsize = Marshal.SizeOf(exinfo);
            exinfo.length = (uint)m_AudioClip.m_Size;
            var result = system.createSound(m_AudioData, MODE.OPENMEMORY | MODE.LOWMEM | MODE.ACCURATETIME, ref exinfo, out var sound);
            if (ErrorCheck(result, ref debugLog))
                return null;

            result = sound.getNumSubSounds(out var numsubsounds);
            if (ErrorCheck(result, ref debugLog))
                return null;

            byte[] buff;
            if (numsubsounds > 0)
            {
                result = sound.getSubSound(0, out var subsound);
                if (ErrorCheck(result, ref debugLog))
                    return null;
                buff = SoundToWav(subsound, ref debugLog);
                subsound.release();
                subsound.clearHandle();
            }
            else
            {
                buff = SoundToWav(sound, ref debugLog);
            }

            sound.release();
            sound.clearHandle();
            return buff;
        }

        private byte[] SoundToWav(Sound sound, ref string debugLog)
        {
            var convertToPcm16 = false;
            var audioFormat = WavAudioFormat.PCM;

            debugLog += "[Fmod] Detecting sound format..\n";
            var result = sound.getFormat(out SOUND_TYPE soundType, out SOUND_FORMAT soundFormat, out int channels, out int bits);
            if (ErrorCheck(result, ref debugLog))
                return null;
            debugLog += $"Detected sound type: {soundType}\n" +
                        $"Detected sound format: {soundFormat}\n" +
                        $"Detected channels: {channels}\n" +
                        $"Detected bit depth: {bits}\n";
            if (soundFormat == SOUND_FORMAT.PCMFLOAT)
            {
                switch (m_AudioClip.m_BitsPerSample)
                {
                    case 16:
                        convertToPcm16 = true;
                        bits = 16;
                        break;
                    case 32:
                        audioFormat = WavAudioFormat.IEEEfloat;
                        break;
                }
            }
            result = sound.getDefaults(out var frequency, out _);
            if (ErrorCheck(result, ref debugLog))
                return null;
            result = sound.getLength(out var length, TIMEUNIT.PCMBYTES);
            if (ErrorCheck(result, ref debugLog))
                return null;
            result = sound.@lock(0, length, out var ptr1, out var ptr2, out var len1, out var len2);
            if (ErrorCheck(result, ref debugLog))
                return null;
            var wavDataLength = convertToPcm16
                ? len1 / 2
                : len1;
            var buffer = new byte[wavDataLength + 44];
            if (convertToPcm16)
            {
                ReadAsPcm16(ptr1, buffer, 44, len1, ref debugLog);
            }
            else
            {
                Marshal.Copy(ptr1, buffer, 44, (int)len1);
            }
            result = sound.unlock(ptr1, ptr2, len1, len2);
            if (ErrorCheck(result, ref debugLog))
                return null;
            //添加wav头
            var wavHeader = new WavHeader(wavDataLength, audioFormat, channels, (uint)frequency, bits);
            wavHeader.WriteToArray(buffer);
            return buffer;
        }

        public byte[] RawAudioClipToWav(ref string debugLog)
        {
            var audioSize = (uint)m_AudioClip.m_Size;
            var channels = m_AudioClip.m_Channels;
            var sampleRate = m_AudioClip.m_Frequency;
            var audioFormat = WavAudioFormat.PCM;
            var bits = 16;

            debugLog += "[Legacy wav converter] Generating wav header..\n";
            var buffer = new byte[audioSize + 44];
            var dataLen = m_AudioClip.m_AudioData.GetData(buffer, 44);
            if (dataLen > 0)
            {
                var wavHeader = new WavHeader(audioSize, audioFormat, channels, (uint)sampleRate, bits);
                wavHeader.WriteToArray(buffer);
            }
            return buffer;
        }

        private static void ReadAsPcm16(IntPtr srcPtr, byte[] destBuffer, int offset, uint pcmDataLen, ref string debugLog)
        {
            var pcmFloatVal = new byte[4];
            for (var i = 0; i < pcmDataLen; i += 4)
            {
                for (var j = 0; j < 4; j++)
                {
                    pcmFloatVal[j] = Marshal.ReadByte(srcPtr, i + j);
                }
                var pcm16Val = (short)MathHelper.Clamp(BitConverter.ToSingle(pcmFloatVal, 0) * short.MaxValue, short.MinValue, short.MaxValue);
                destBuffer[offset] = (byte)(pcm16Val & 255);
                destBuffer[offset + 1] = (byte)(pcm16Val >> 8);
                offset += 2;
            }
            debugLog += "Finished PCMFLOAT -> PCM16 converting\n";
        }

        private static bool ErrorCheck(RESULT result, ref string log)
        {
            if (result != RESULT.OK)
            {
                log += $"FMOD error! {result} - {Error.String(result)}\n";
                return true;
            }
            return false;
        }

        public string GetExtensionName()
        {
            if (m_AudioClip.version < 5)
            {
                switch (m_AudioClip.m_Type)
                {
                    case FMODSoundType.AAC:
                        return ".m4a";
                    case FMODSoundType.AIFF:
                        return ".aif";
                    case FMODSoundType.IT:
                        return ".it";
                    case FMODSoundType.MOD:
                        return ".mod";
                    case FMODSoundType.MPEG:
                        return ".mp3";
                    case FMODSoundType.OGGVORBIS:
                        return ".ogg";
                    case FMODSoundType.S3M:
                        return ".s3m";
                    case FMODSoundType.WAV:
                        return ".wav";
                    case FMODSoundType.XM:
                        return ".xm";
                    case FMODSoundType.XMA:
                        return ".wav";
                    case FMODSoundType.VAG:
                        return ".vag";
                    case FMODSoundType.AUDIOQUEUE:
                        return ".fsb";
                }

            }
            else
            {
                switch (m_AudioClip.m_CompressionFormat)
                {
                    case AudioCompressionFormat.PCM:
                        return ".fsb";
                    case AudioCompressionFormat.Vorbis:
                        return ".fsb";
                    case AudioCompressionFormat.ADPCM:
                        return ".fsb";
                    case AudioCompressionFormat.MP3:
                        return ".fsb";
                    case AudioCompressionFormat.PSMVAG:
                        return ".fsb";
                    case AudioCompressionFormat.HEVAG:
                        return ".fsb";
                    case AudioCompressionFormat.XMA:
                        return ".fsb";
                    case AudioCompressionFormat.AAC:
                        return ".m4a";
                    case AudioCompressionFormat.GCADPCM:
                        return ".fsb";
                    case AudioCompressionFormat.ATRAC9:
                        return ".fsb";
                }
            }
            return ".AudioClip";
        }
    }
}
