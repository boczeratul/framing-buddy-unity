using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace FramingBuddy.EditorTools
{
    /// <summary>
    /// 效能分析（batchmode）：讀取播放器錄下的 .raw 檔，列出主執行緒與渲染執行緒自身耗時最高的標記。
    /// -executeMethod FramingBuddy.EditorTools.ProfileReport.Run -profileFile &lt;path.raw&gt;
    /// </summary>
    public static class ProfileReport
    {
        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-profileFile");
            string path = args[i + 1];
            ProfilerDriver.LoadProfile(path, false);
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            Debug.Log($"[profile] frames {first}..{last}");
            foreach (int thread in new[] { 0, 1 })
            {
                var self = new Dictionary<string, double>();
                var total = new Dictionary<string, double>();
                string threadName = "";
                int frames = 0;
                for (int f = first; f <= last; f++)
                {
                    using var view = ProfilerDriver.GetRawFrameDataView(f, thread);
                    if (view == null || !view.valid) continue;
                    threadName = view.threadName;
                    frames++;
                    int n = view.sampleCount;
                    var children = new double[n];
                    // 自身時間 = 總時間 − 直接子樣本時間
                    var stack = new Stack<(int idx, int remaining)>();
                    for (int s = 0; s < n; s++)
                    {
                        double t = view.GetSampleTimeMs(s);
                        string name = view.GetSampleName(s);
                        total[name] = total.TryGetValue(name, out var tt) ? tt + t : t;
                        while (stack.Count > 0 && stack.Peek().remaining == 0) stack.Pop();
                        if (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            children[top.idx] += t;
                            stack.Push((top.idx, top.remaining - 1));
                        }
                        int kids = view.GetSampleChildrenCount(s);
                        stack.Push((s, kids));
                        self[name] = (self.TryGetValue(name, out var ss) ? ss : 0) + t;
                        children[s] = 0;
                    }
                    // 扣掉子樣本
                    stack.Clear();
                    for (int s = 0; s < n; s++)
                    {
                        double t = view.GetSampleTimeMs(s);
                        while (stack.Count > 0 && stack.Peek().remaining == 0) stack.Pop();
                        if (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            string pn = view.GetSampleName(top.idx);
                            self[pn] -= t;
                            stack.Push((top.idx, top.remaining - 1));
                        }
                        stack.Push((s, view.GetSampleChildrenCount(s)));
                    }
                }
                if (frames == 0) continue;
                Debug.Log($"[profile] thread {thread} {threadName}: {frames} frames");
                foreach (var kv in self.OrderByDescending(kv => kv.Value).Take(40))
                    Debug.Log($"[profile]   self {kv.Value / frames,7:0.000} ms  total {total[kv.Key] / frames,7:0.000} ms  {kv.Key}");
            }
            EditorApplication.Exit(0);
        }
    }
}
