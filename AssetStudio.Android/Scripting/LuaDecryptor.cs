using System;
using MoonSharp.Interpreter;

namespace AssetStudioMobile.Scripting
{
    /// <summary>
    /// Runs a user-supplied Lua script that gets a chance to rewrite a file's bytes before the
    /// loader sees them.
    ///
    /// This exists because encrypted bundles fail in a way no amount of tooling can guess at: the
    /// container is XORed, or has a header, or is wrapped in something, and the scheme is per game.
    /// The script is how that scheme gets expressed without shipping a reverse engineering job.
    ///
    /// The contract is one global function:
    ///
    ///     function decrypt(data, name)
    ///         -- return the new bytes, or nil to say "this file is not mine, leave it alone"
    ///     end
    ///
    /// Binary safety is the whole difficulty here. Lua strings are byte arrays, but MoonSharp maps
    /// them onto .NET strings, so bytes become chars. Both directions of that mapping are done
    /// explicitly below -- one byte to one char and back -- because anything that goes through the
    /// runtime's own string conversion (UTF-8, an Encoding, a JsonValue) corrupts every byte above
    /// 0x7F and any zero.
    /// </summary>
    internal sealed class LuaDecryptor
    {
        private readonly Script _script;
        private readonly DynValue _decrypt;
        private readonly string _path;

        public string Name => System.IO.Path.GetFileName(_path);

        /// <summary>True when the script defined a decrypt function, so there is work to do.</summary>
        public bool Active { get; }

        private LuaDecryptor(Script script, DynValue decrypt, string path, bool active)
        {
            _script = script;
            _decrypt = decrypt;
            _path = path;
            Active = active;
        }

        /// <summary>
        /// Loads and runs a script, returning null with <paramref name="error"/> set if it does not
        /// compile or has no decrypt function. A script that only defines helpers and calls nothing
        /// is loaded but inactive.
        /// </summary>
        public static LuaDecryptor Load(string path, out string error)
        {
            error = null;
            try
            {
                // No io, no os, no require: a decryption script needs string, table, math and bit32,
                // and nothing else it could ask for is worth the blast radius.
                var script = new Script(CoreModules.Preset_SoftSandbox);
                script.DoFile(path);

                var decrypt = script.Globals.Get("decrypt");
                var active = decrypt != null && decrypt.Type == DataType.Function;
                if (!active)
                {
                    error = "脚本里没有找到 decrypt(data, name) 函数";
                    return null;
                }

                return new LuaDecryptor(script, decrypt, path, true);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Offers the bytes to the script. Returns false -- with <paramref name="result"/> null --
        /// when the script wants the file left alone; returns false with <paramref name="error"/>
        /// set when the script failed.
        /// </summary>
        public bool TryTransform(byte[] data, int length, string fileName,
                                 out byte[] result, out string error)
        {
            result = null;
            error = null;

            try
            {
                var input = ToLuaString(data, length);
                var returned = _script.Call(_decrypt, DynValue.NewString(input), DynValue.NewString(fileName));

                if (returned == null || returned.Type == DataType.Nil || returned.Type == DataType.Void)
                {
                    return false;
                }

                if (returned.Type != DataType.String)
                {
                    error = $"decrypt 返回了 {returned.Type}，只接受 string 或 nil";
                    return false;
                }

                var output = FromLuaString(returned.String);
                if (output.Length == 0)
                {
                    error = "decrypt 返回了空字符串";
                    return false;
                }

                result = output;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static string ToLuaString(byte[] data, int length)
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = (char)data[i];
            return new string(chars);
        }

        private static byte[] FromLuaString(string value)
        {
            var bytes = new byte[value.Length];
            for (var i = 0; i < value.Length; i++) bytes[i] = (byte)value[i];
            return bytes;
        }
    }
}
