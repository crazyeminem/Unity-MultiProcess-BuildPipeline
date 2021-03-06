﻿using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MultiProcessBuild
{
    public static partial class BuildPipeline
    {
        static AssetBundleManifest OutputResult(string resultFile, float useTime, UnityEngine.AssetBundleManifest manifest)
        {
            if (manifest == null)
                return null;

            AssetBundleManifest result = new AssetBundleManifest();
            result.buildTime = useTime;
            List<AssetBundleManifest.AssetBundleBuild> bundles = new List<AssetBundleManifest.AssetBundleBuild>();
            foreach (var name in manifest.GetAllAssetBundles())
            {
                AssetBundleManifest.AssetBundleBuild bundle = new AssetBundleManifest.AssetBundleBuild();
                bundle.assetBundleName = name;
                bundle.dependency = manifest.GetDirectDependencies(name);
                bundle.hash = manifest.GetAssetBundleHash(name).ToString();
                bundles.Add(bundle);
            }
            result.builds = bundles.ToArray();
            File.WriteAllText(resultFile, JsonUtility.ToJson(result, true));
            return result;
        }

        [MenuItem("MultiProcessBuild/Build With buid.json")]
        static void BuildJobSlave()
        {
            string text = File.ReadAllText("./build.json");
            BuildJob job = JsonUtility.FromJson<BuildJob>(text);
            BuildJob(job);
        }

        static AssetBundleManifest BuildJob(BuildJob job)
        {
            long ot = System.DateTime.Now.Ticks;
            var unity_manifest = job.Build();
            string resultFile = string.Format("{0}/result_{1}.json", job.output, job.slaveID);
            return OutputResult(resultFile, (System.DateTime.Now.Ticks - ot) / 10000000f, unity_manifest);
        }

        public static AssetBundleManifest BuildAssetBundles(string output, AssetBundleBuild[] builds, BuildAssetBundleOptions options, BuildTarget target)
        {
            output = Path.GetFullPath(output);

            var tree = new BuildTree();
            foreach (var build in builds)
            {
                foreach (var asset in build.assetNames)
                {
                    tree.AddBuildAsset(asset, build.assetBundleName);
                }
            }

            List<string> slaves = new List<string>();
            int i = 0;
            string slaveRoot = Path.GetFullPath(Profile.SlaveRoot);
            while (true)
            {
                string slaveProj = Path.Combine(slaveRoot, string.Format("slave_{0}", i++));
                if (!Directory.Exists(slaveProj))
                    break;
                slaves.Add(slaveProj);
            }

            //for debug
            var depTree = tree.GetDependencyTree();
            File.WriteAllText("Temp/deps.json", JsonUtility.ToJson(depTree, true));

            string Unity = EditorApplication.applicationPath;
            var jobs = tree.BuildJobs(slaves.Count + 1, output, options, target);
            List<Process> pss = new List<Process>();
            AssetBundleManifest[] results = new AssetBundleManifest[jobs.Length];
            for (int jobID = 1; jobID < jobs.Length; ++jobID)
            {
                int slaveID = jobID - 1;
                BuildJob job = jobs[jobID];
                string slaveProj = slaves[slaveID];
                File.WriteAllText(slaveProj + "/build.json", JsonUtility.ToJson(job, true));
                string cmd = string.Format(" -quit" +
                                           " -batchmode" +
                                           " -logfile {0}/log.txt" +
                                           " -projectPath {0} " +
                                           " -executeMethod MultiProcessBuild.BuildPipeline.BuildJobSlave",
                                           slaveProj);
                var ps = Process.Start(Unity, cmd);
                pss.Add(ps);
            }

            bool allFinish = true;
            if (jobs.Length > 0)
            {
                var job = jobs[0];
                File.WriteAllText("build.json", JsonUtility.ToJson(job, true));

                long ot = System.DateTime.Now.Ticks;
                var result = BuildJob(job);
                results[0] = result;
                if (result == null)
                    allFinish = false;
            }
            for (int slaveID = 0; slaveID < pss.Count; ++slaveID)
            {
                var ps = pss[slaveID];
                ps.WaitForExit();
                var ExitCode = ps.ExitCode;
                if (ExitCode != 0)
                {
                    allFinish = false;
                    UnityEngine.Debug.LogErrorFormat("slave {0} code:{1}", slaveID, ExitCode);
                }
                else
                {
                    UnityEngine.Debug.LogFormat("slave {0} code:{1}", slaveID, ExitCode);
                    string resultFile = string.Format(string.Format("{0}/result_{1}.json", output, slaveID + 1));
                    results[slaveID + 1] = JsonUtility.FromJson<AssetBundleManifest>(File.ReadAllText(resultFile));
                }
                ps.Dispose();
            }

            if (allFinish)
            {
                AssetBundleManifest manifest = new AssetBundleManifest();
                manifest.buildTime = 0f;
                List<AssetBundleManifest.AssetBundleBuild> totalBuilds = new List<AssetBundleManifest.AssetBundleBuild>();
                foreach (var result in results)
                {
                    totalBuilds.AddRange(result.builds);
                    manifest.buildTime = Mathf.Max(manifest.buildTime, result.buildTime);
                }
                totalBuilds.Sort((a, b) => { return a.assetBundleName.CompareTo(b.assetBundleName); });
                manifest.builds = totalBuilds.ToArray();
                File.WriteAllText(string.Format("{0}/result.json", output), JsonUtility.ToJson(manifest, true));
                UnityEngine.Debug.LogFormat("all slave finish.");
                return manifest;
            }
            else
            {
                UnityEngine.Debug.LogErrorFormat("some slave error.");
                return null;
            }
        }
    }
}