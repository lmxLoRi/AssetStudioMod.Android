using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using Object = AssetStudio.Object;

namespace AssetStudioMobile
{
    /// <summary>
    /// Collects skeleton animations -- Spine and DragonBones -- into one folder per model.
    ///
    /// There is nothing to copy here from the desktop, because the desktop has no Spine support at
    /// all: no extractor, no grouping, no mention of the word. It writes the parts by name and the
    /// extension work is what made them readable; the files arrive already complete.
    ///
    /// So this is not a parser. A skeleton's parts are separate assets that already contain
    /// everything, and what is missing is that they are scattered: the atlas describes the pages, the
    /// skeleton names the atlas, the texture holds the picture, and they sit in the output next to
    /// thousands of unrelated files. Grouping them is the whole job.
    ///
    /// Two naming schemes, both from what the games call things:
    ///     Spine        "model.atlas"  "model.json"  "model.png"    (the atlas names its page)
    ///     DragonBones  "model_ske.json"  "model_tex.json"  "model_tex.png"
    /// </summary>
    internal static class SkeletonExport
    {
        private sealed class Model
        {
            public string Base;
            public bool DragonBones;
            public TextAsset Skeleton;
            public string SkeletonName;
            public TextAsset Atlas;
            public string AtlasName;
            public readonly List<string> TextureNames = new List<string>();
        }

        public static int Export(AssetsManager manager,
                                 IReadOnlyDictionary<Object, string> containers,
                                 string outputRoot,
                                 ExportOptions options,
                                 Action<string> log)
        {
            var models = Find(manager, containers);
            if (models.Count == 0)
            {
                log("没有找到 Spine 或 DragonBones 骨骼动画");
                return 0;
            }

            log($"找到 {models.Count} 个骨骼动画（Spine {models.Values.Count(m => !m.DragonBones)} 个，" +
                $"DragonBones {models.Values.Count(m => m.DragonBones)} 个）");

            // A texture is only written for a model that named it, so the lookup is built once.
            var textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (obj is Texture2D texture && !string.IsNullOrEmpty(texture.m_Name))
                        textures[texture.m_Name] = texture;
                }
            }

            var written = 0;
            foreach (var model in models.Values.OrderBy(m => m.Base, StringComparer.Ordinal))
            {
                try
                {
                    var folder = Path.Combine(outputRoot, model.DragonBones ? "DragonBones" : "Spine",
                                              Safe(model.Base));
                    Directory.CreateDirectory(folder);

                    if (model.Skeleton != null) Write(folder, model.SkeletonName, model.Skeleton.m_Script);
                    if (model.Atlas != null) Write(folder, model.AtlasName, model.Atlas.m_Script);

                    var missing = 0;
                    foreach (var name in model.TextureNames)
                    {
                        if (!textures.TryGetValue(name, out var texture)) { missing++; continue; }

                        var dest = Path.Combine(folder, name + ".png");
                        var plan = Extractor.Plan(texture, options, containers);
                        if (plan == null) { missing++; continue; }

                        plan.Write(texture, dest, options);
                    }

                    written++;
                    if (missing > 0) log($"{model.Base}：有 {missing} 个贴图没找到");
                }
                catch (Exception ex)
                {
                    log($"骨骼动画导出失败（{model.Base}）：{ex.GetType().Name}: {ex.Message}");
                }
            }

            log($"骨骼动画：已导出 {written} 个模型到 {outputRoot}");
            return written;
        }

        /// <summary>
        /// The names an asset might be known by, best first.
        ///
        /// Both are needed, because the two families keep their extension in different places. A
        /// Spine part carries it in its own name ("M095_Spine.atlas"); a DragonBones part does not
        /// ("M098_Model_ske") and is published with it (".../M098_Model_ske.json"). Classifying by
        /// either one alone found half a tree and reported the other half as empty -- 28 Spine and
        /// no DragonBones, then 42 DragonBones and no Spine.
        /// </summary>
        private static List<string> LogicalNames(TextAsset asset, IReadOnlyDictionary<Object, string> containers)
        {
            var names = new List<string>(2);
            if (!string.IsNullOrEmpty(asset.m_Name)) names.Add(asset.m_Name);

            if (containers != null && containers.TryGetValue(asset, out var container)
                && !string.IsNullOrEmpty(container))
            {
                var file = Path.GetFileName(container);
                if (!string.IsNullOrEmpty(file) && !names.Contains(file)) names.Add(file);
            }

            return names;
        }

        /// <summary>Groups the assets by the model their names say they belong to.</summary>
        private static Dictionary<string, Model> Find(AssetsManager manager,
                                                      IReadOnlyDictionary<Object, string> containers)
        {
            var models = new Dictionary<string, Model>(StringComparer.Ordinal);
            var skeletons = new List<(string Base, TextAsset Asset)>();

            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is TextAsset asset) || string.IsNullOrEmpty(asset.m_Name)) continue;

                    // The first name that matches a known shape decides what the asset is.
                    foreach (var candidate in LogicalNames(asset, containers))
                    {
                        if (Classify(candidate, asset, models)) break;
                    }
                }
            }

            return models;
        }

        /// <summary>Sorts one asset into a model, and says whether its name was recognised.</summary>
        private static bool Classify(string name, TextAsset asset, Dictionary<string, Model> models)
        {
            Model ModelFor(string baseName, bool dragonBones)
            {
                if (!models.TryGetValue(baseName, out var model))
                {
                    model = new Model { Base = baseName, DragonBones = dragonBones };
                    models[baseName] = model;
                }
                return model;
            }

            // DragonBones first: its names also end in ".json".
            if (name.EndsWith("_ske.json", StringComparison.OrdinalIgnoreCase))
            {
                var model = ModelFor(name.Substring(0, name.Length - "_ske.json".Length), true);
                model.Skeleton = asset;
                model.SkeletonName = name;
                return true;
            }

            if (name.EndsWith("_tex.json", StringComparison.OrdinalIgnoreCase))
            {
                var model = ModelFor(name.Substring(0, name.Length - "_tex.json".Length), true);
                model.Atlas = asset;
                model.AtlasName = name;
                model.TextureNames.Add(model.Base + "_tex");
                return true;
            }

            foreach (var suffix in new[] { ".atlas.txt", ".atlas" })
            {
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                var model = ModelFor(name.Substring(0, name.Length - suffix.Length), false);
                model.Atlas = asset;
                model.AtlasName = name;

                // A Spine atlas opens with the page's image file name.
                var page = FirstLine(asset.m_Script);
                if (!string.IsNullOrEmpty(page))
                    model.TextureNames.Add(Path.GetFileNameWithoutExtension(page));
                return true;
            }

            // A plain ".json" is a skeleton only if an atlas already claimed that base name.
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var stem = name.Substring(0, name.Length - ".json".Length);
                if (!models.TryGetValue(stem, out var model)) return false;

                model.Skeleton = asset;
                model.SkeletonName = name;
                return true;
            }

            // A Spine skeleton is often named with no extension in either place: m_Name is
            // "M095_Spine" and the container is ".../M095_Spine.asset", while the content is the
            // skeleton JSON. A model whose base name it matches is what it belongs to.
            var bare = Path.GetFileNameWithoutExtension(name);
            if (!string.IsNullOrEmpty(bare) && models.TryGetValue(bare, out var owner)
                && owner.Skeleton == null)
            {
                owner.Skeleton = asset;
                owner.SkeletonName = bare + ".json";
                return true;
            }

            return false;
        }

        private static string FirstLine(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            var end = 0;
            while (end < bytes.Length && bytes[end] != (byte)'\n' && bytes[end] != (byte)'\r') end++;
            if (end == 0) return null;

            try { return System.Text.Encoding.UTF8.GetString(bytes, 0, end).Trim(); }
            catch { return null; }
        }

        private static void Write(string folder, string name, byte[] bytes)
            => File.WriteAllBytes(Path.Combine(folder, Safe(name)), bytes);

        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";

            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                builder.Append(c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);

            return builder.ToString();
        }
    }
}
