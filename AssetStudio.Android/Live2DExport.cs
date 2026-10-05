using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using AssetStudio;
using CubismLive2DExtractor;
using static CubismLive2DExtractor.CubismParsers;
using Object = AssetStudio.Object;

namespace AssetStudioMobile
{
    /// <summary>
    /// Finds the Live2D Cubism models in what has been loaded and writes each one out as a folder.
    ///
    /// The assembly of a model -- moc3, model3.json, physics3.json, motions, expressions, textures --
    /// is done by CubismLive2DExtractor, which is the desktop's own extractor and lives in
    /// AssetStudioUtility, so it is already referenced here. What this adds is the part the desktop
    /// does in its Studio class: recognise a model's MonoBehaviour components, find the assets
    /// published alongside it, and hand each model its group.
    ///
    /// The model's own binding (CubismModel, its physics, fade and expression controllers) is already
    /// done by AssetsManager while it reads, so only the parts it does not know about are bound here.
    /// </summary>
    internal static class Live2DExport
    {
        public static int Export(AssetsManager manager,
                                 IReadOnlyDictionary<Object, string> containers,
                                 string outputRoot,
                                 Action<string> log)
        {
            var objects = manager.AssetsFileList.Sum(f => f.Objects.Count);
            log($"已加载 {manager.AssetsFileList.Count} 个文件、{objects} 个对象");

            var models = Find(manager);
            if (models.Count == 0)
            {
                log("没有找到 Live2D Cubism 模型");
                return 0;
            }

            log($"找到 {models.Count} 个 Live2D 模型");

            var paths = ModelPaths(models, containers);
            var groups = Group(models, paths, containers);

            if (groups.Count == 0)
            {
                log("模型都没有可归组的资源：它们的发布路径（容器路径）不可用");
                return 0;
            }

            Live2DExtractor.MocDict = models;

            // No AssemblyLoader: it needs the game's Assembly-CSharp.dll, which a phone has no way to
            // reach. Reading a MonoBehaviour's fields then depends on the bundle carrying a type tree.
            var written = 0;
            foreach (var group in groups)
            {
                try
                {
                    var extractor = new Live2DExtractor(group);
                    var name = ModelName(extractor, group.Key);
                    var destination = Path.Combine(outputRoot, name);

                    extractor.ExtractCubismModel(destination, Live2DMotionMode.MonoBehaviour);
                    written++;
                    log($"Live2D 已导出：{name}");
                }
                catch (Exception ex)
                {
                    log($"Live2D 导出失败（{group.Key.m_Name}）：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return written;
        }

        /// <summary>Every moc found, with its model when one could be resolved.</summary>
        private static Dictionary<MonoBehaviour, CubismModel> Find(AssetsManager manager)
        {
            var models = new Dictionary<MonoBehaviour, CubismModel>();
            var mocs = new List<MonoBehaviour>();
            var bindings = new List<(MonoBehaviour Behaviour, CubismMonoBehaviourType Type, bool IsParameter)>();

            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    switch (obj)
                    {
                        case MonoBehaviour behaviour when behaviour.m_Script.TryGet(out var script):
                            switch (script.m_ClassName)
                            {
                                case "CubismMoc":
                                    mocs.Add(behaviour);
                                    break;
                                case "CubismRenderer":
                                    bindings.Add((behaviour, CubismMonoBehaviourType.RenderTexture, false));
                                    break;
                                case "CubismDisplayInfoParameterName":
                                    bindings.Add((behaviour, CubismMonoBehaviourType.DisplayInfo, true));
                                    break;
                                case "CubismDisplayInfoPartName":
                                    bindings.Add((behaviour, CubismMonoBehaviourType.DisplayInfo, false));
                                    break;
                                case "CubismPosePart":
                                    bindings.Add((behaviour, CubismMonoBehaviourType.PosePart, false));
                                    break;
                            }
                            break;

                        case GameObject gameObject when gameObject.CubismModel != null:
                            if (TryGetMoc(gameObject.CubismModel.CubismModelMono, out var moc))
                                models[moc] = gameObject.CubismModel;
                            break;
                    }
                }
            }

            // A moc with no model GameObject still gets an entry: the extractor reports on it.
            foreach (var moc in mocs)
            {
                if (!models.ContainsKey(moc)) models[moc] = null;
            }

            foreach (var (behaviour, type, isParameter) in bindings) Bind(behaviour, type, isParameter);

            return models;
        }

        /// <summary>The CubismMoc a CubismModel component points at.</summary>
        private static bool TryGetMoc(MonoBehaviour behaviour, out MonoBehaviour moc)
        {
            moc = null;
            if (behaviour == null) return false;

            try
            {
                var parsed = CubismParsers.ParseMonoBehaviour(behaviour,
                    CubismMonoBehaviourType.Model, null);

                if (!(parsed?["_moc"] is OrderedDictionary reference)) return false;

                var pptr = new PPtr<MonoBehaviour>
                {
                    m_FileID = (int)reference["m_FileID"],
                    m_PathID = (long)reference["m_PathID"],
                    AssetsFile = behaviour.assetsFile,
                };

                return pptr.TryGet(out moc);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Attaches a component to the model its transform belongs to.</summary>
        private static void Bind(MonoBehaviour behaviour, CubismMonoBehaviourType type, bool isParameter)
        {
            if (!behaviour.m_GameObject.TryGet(out var owner)) return;
            if (!TryGetModelGameObject(owner.m_Transform, out var model) || model.CubismModel == null) return;

            switch (type)
            {
                case CubismMonoBehaviourType.PosePart:
                    model.CubismModel.PosePartList.Add(behaviour);
                    break;
                case CubismMonoBehaviourType.DisplayInfo when isParameter:
                    model.CubismModel.ParamDisplayInfoList.Add(behaviour);
                    break;
                case CubismMonoBehaviourType.DisplayInfo:
                    model.CubismModel.PartDisplayInfoList.Add(behaviour);
                    break;
                case CubismMonoBehaviourType.RenderTexture:
                    model.CubismModel.RenderTextureList.Add(behaviour);
                    break;
            }
        }

        /// <summary>Walks up from a component to the GameObject that carries the model.</summary>
        private static bool TryGetModelGameObject(Transform transform, out GameObject gameObject)
        {
            gameObject = null;
            if (transform == null) return false;

            while (transform.m_Father.TryGet(out var father))
            {
                transform = father;
                if (transform.m_GameObject.TryGet(out gameObject) && gameObject.CubismModel != null)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Where each model is published, from the container path of its moc.
        ///
        /// When several mocs share a directory, the whole path is used instead of the directory, so
        /// two models in one folder cannot collect each other's assets.
        /// </summary>
        private static Dictionary<MonoBehaviour, string> ModelPaths(
            Dictionary<MonoBehaviour, CubismModel> models, IReadOnlyDictionary<Object, string> containers)
        {
            var found = new Dictionary<MonoBehaviour, (string Full, string Directory)>();

            foreach (var moc in models.Keys)
            {
                if (!containers.TryGetValue(moc, out var full)) continue;

                var cut = full.LastIndexOf('/');
                found[moc] = (full, cut > 0 ? full.Substring(0, cut) : full);
            }

            var paths = new Dictionary<MonoBehaviour, string>();
            if (found.Count == 0) return paths;

            var directories = found.Values.Select(v => v.Directory).ToHashSet();
            var useFullPath = found.Count != directories.Count;

            foreach (var pair in found)
            {
                if (models.ContainsKey(pair.Key))
                    paths[pair.Key] = useFullPath ? pair.Value.Full : pair.Value.Directory;
            }

            return paths;
        }

        /// <summary>
        /// For each model, the assets published under the same path -- the model's own name appearing
        /// as one of the path's segments is what keeps the parts of two models in one folder apart.
        /// </summary>
        private static Dictionary<MonoBehaviour, List<Object>> Group(
            Dictionary<MonoBehaviour, CubismModel> models,
            Dictionary<MonoBehaviour, string> paths,
            IReadOnlyDictionary<Object, string> containers)
        {
            var groups = new Dictionary<MonoBehaviour, List<Object>>();

            foreach (var pair in paths)
            {
                var modelPath = pair.Value;
                var modelName = modelPath.Substring(modelPath.LastIndexOf('/') + 1);
                var members = new List<Object>();

                foreach (var candidate in containers)
                {
                    if (!candidate.Value.Contains(modelPath)) continue;
                    if (!candidate.Value.Split('/').Contains(modelName)) continue;
                    members.Add(candidate.Key);
                }

                if (members.Count > 0) groups[pair.Key] = members;
            }

            return groups;
        }

        /// <summary>The model's name, or the moc's if the model itself has none.</summary>
        private static string ModelName(Live2DExtractor extractor, MonoBehaviour moc)
        {
            var name = extractor.Model?.Name;

            if (string.IsNullOrEmpty(name))
            {
                var file = moc.assetsFile;
                var path = string.IsNullOrEmpty(file?.originalPath) ? file?.fileName : file.originalPath;
                name = string.IsNullOrEmpty(path) ? moc.m_Name : Path.GetFileNameWithoutExtension(path);
            }

            if (string.IsNullOrEmpty(name)) name = "model";

            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                builder.Append(c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);

            return builder.ToString();
        }
    }
}
