using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public static partial class JsonConverterHelper
    {
        /// <summary>
        /// Writes the KeyValuePair&lt;object, object&gt; entries the type tree builds for Unity's pair
        /// nodes (TypeTreeHelper.ReadValue creates a List of them), in the
        /// { "Key": ..., "Value": ... } shape System.Text.Json normally produces and the matching
        /// read converter expects.
        ///
        /// System.Text.Json cannot build metadata for KeyValuePair on this runtime: its constructor
        /// parameters carry no names, so DefaultJsonTypeInfoResolver throws
        /// ConstructorContainsNullParameterNames. That lands while *serialising* the type tree, and
        /// the object being loaded is dropped for it -- 6587 Materials in one game cache, every
        /// object whose type tree contains a pair.
        ///
        /// Deliberately not a JsonConverterFactory over KeyValuePair&lt;,&gt;: only this one closed type
        /// appears on the write side, and a factory would need MakeGenericType plus
        /// Activator.CreateInstance, which is exactly the reflection that trimming removes.
        /// </summary>
        public sealed class KeyValuePairObjectConverter : JsonConverter<KeyValuePair<object, object>>
        {
            public override void Write(Utf8JsonWriter writer, KeyValuePair<object, object> value,
                                       JsonSerializerOptions options)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("Key");
                WriteValue(writer, value.Key, options);

                writer.WritePropertyName("Value");
                WriteValue(writer, value.Value, options);

                writer.WriteEndObject();
            }

            private static void WriteValue(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value == null)
                    writer.WriteNullValue();
                else
                    JsonSerializer.Serialize(writer, value, value.GetType(), options);
            }

            public override KeyValuePair<object, object> Read(ref Utf8JsonReader reader, Type typeToConvert,
                                                              JsonSerializerOptions options)
                => throw new NotSupportedException("the type tree is only ever written");
        }
    }
}
