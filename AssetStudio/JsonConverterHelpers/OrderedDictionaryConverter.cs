using System;
using System.Collections;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public static partial class JsonConverterHelper
    {
        /// <summary>
        /// Writes the OrderedDictionary that the type tree reader builds, one entry at a time.
        ///
        /// Without this, System.Text.Json takes its non-generic IDictionary path, which is typed
        /// KeyValuePair&lt;object, object&gt; internally, and asks the polymorphic resolver for metadata
        /// for that pair. On the mobile runtime KeyValuePair's constructor parameters carry no names,
        /// so that throws ConstructorContainsNullParameterNames and the whole object is dropped:
        /// 6587 Materials in one game cache, i.e. every object whose type tree nests a dictionary.
        /// The exception is raised while *serialising* the type tree, in
        /// TypeTreeHelper.ReadTypeByteArray, nowhere near the class being loaded.
        ///
        /// Writing the entries here never asks for that metadata. Values go out by their runtime
        /// type so a nested OrderedDictionary comes back through this converter rather than down
        /// the polymorphic path again.
        /// </summary>
        public sealed class OrderedDictionaryConverter : JsonConverter<OrderedDictionary>
        {
            public override void Write(Utf8JsonWriter writer, OrderedDictionary value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();

                foreach (DictionaryEntry entry in value)
                {
                    writer.WritePropertyName(entry.Key?.ToString() ?? string.Empty);

                    if (entry.Value == null)
                        writer.WriteNullValue();
                    else
                        JsonSerializer.Serialize(writer, entry.Value, entry.Value.GetType(), options);
                }

                writer.WriteEndObject();
            }

            public override OrderedDictionary Read(ref Utf8JsonReader reader, Type typeToConvert,
                                                   JsonSerializerOptions options)
                => throw new NotSupportedException("the type tree is only ever written");
        }
    }
}
