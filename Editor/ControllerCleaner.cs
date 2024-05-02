using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace DreadScripts.ControllerCleaner
{
    public class ControllerCleaner : EditorWindow
    {
        private static AnimatorController scanOneController;
        private static readonly List<ScanResult> results = new List<ScanResult>();
        private static Vector2 scroll;

        [MenuItem("DreadTools/Utility/Controller Cleaner")]
        private static void ShowWindow()
             => GetWindow<ControllerCleaner>(false, "Controller Cleaner", true)
                 .titleContent.image = EditorGUIUtility.IconContent("Grid.PaintTool").image;

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            using (new GUILayout.HorizontalScope("box"))
            {
                scanOneController = (AnimatorController) EditorGUILayout.ObjectField("Controller", scanOneController, typeof(AnimatorController), false);
                using (new EditorGUI.DisabledScope(!scanOneController))
                    if (GUILayout.Button("Scan", Styles.toolbarButton, GUILayout.ExpandWidth(false)))
                        ScanOne();
            }

            if (GUILayout.Button("Scan All Controllers", Styles.toolbarButton)) ScanAll();

            if (results.Count > 0)
            {
                DrawSeperator();
                if (GUILayout.Button("Clean All", Styles.toolbarButton))
                {
                    foreach (var r in results) r.CleanUpController();
                    GreenLog("Finished Cleanup!");
                }
                foreach (var r in results)
                    r.Display();
            }

            Credit();
            EditorGUILayout.EndScrollView();
        }

        private void ScanOne()
        {
            results.Remove(results.FirstOrDefault(r => r.controller == scanOneController));
            results.Insert(0, new ScanResult(scanOneController));
        }
        private void ScanAll()
        {
            results.Clear();
            var allControllers = AssetDatabase.FindAssets($"t:{nameof(AnimatorController)}").Select(g => AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GUIDToAssetPath(g)));
            foreach (var c in allControllers)
                results.Add(new ScanResult(c));
        }

        private static void DrawSeperator(int thickness = 2, int padding = 10, int maxWidth = 0)
        {
            var r = maxWidth > 0 ?
                EditorGUILayout.GetControlRect(GUILayout.Height(thickness + padding), GUILayout.MaxWidth(maxWidth)) :
                EditorGUILayout.GetControlRect(GUILayout.Height(thickness + padding));

            r.height = thickness;
            r.y += padding / 2f;
            r.x -= 2;
            r.width += 6;
            ColorUtility.TryParseHtmlString(EditorGUIUtility.isProSkin ? "#595959" : "#858585", out Color lineColor);
            EditorGUI.DrawRect(r, lineColor);
        }

        private static void GreenLog(string msg) 
            => Debug.Log($"<color=green>[ControllerCleaner]</color> {msg}");
        private static void Credit()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Made By Dreadrith#3238", "boldlabel"))
                    Application.OpenURL("https://linktr.ee/Dreadrith");
            }
        }


        private class ScanResult
        {
            public AnimatorController controller;
            private ConcurrentBag<Object> usedObjects;
            private Object[] obsoleteObjects;
            private Object[] allObjects;
            private string failMessage;
            private bool finished;
            private bool finalizing;
            private bool completed;
            private bool failed;
            private bool cancelled;
            private bool isClean => obsoleteObjects.Length == 0;

            private string scanTimeString => $"({stopwatch.Elapsed.TotalMilliseconds / 1000f:0.0})";
            private readonly Stopwatch stopwatch = new Stopwatch();
            private CancellationTokenSource tokenSource;

            private static readonly Type[] scanTypes =
            {
                typeof(AnimatorState),
                typeof(AnimatorStateTransition),
                typeof(AnimatorStateMachine),
                typeof(AnimatorTransition),
                typeof(AnimatorTransitionBase),
                typeof(StateMachineBehaviour),
                typeof(BlendTree),
            };

            public ScanResult(AnimatorController target) => ScanController(target);
            public void Display()
            {
                const float iconWidth = 18;
                const float iconHeight = 18;
                const float buttonWidth = 55;

                GUILayoutOption[] iconOptions = new GUILayoutOption[] { GUILayout.Width(iconWidth), GUILayout.Height(iconHeight) };
                GUILayoutOption[] buttonOptions = new GUILayoutOption[] {GUILayout.Width(buttonWidth)};

                if (finalizing && !cancelled && !completed) FinalizeScan();

                using (new GUILayout.HorizontalScope("box"))
                {
                    if (GUILayout.Button(controller ? controller.name : "[Missing]", GUI.skin.label))
                        if (controller) EditorGUIUtility.PingObject(controller);

                    if (failed) GUILayout.Label(new GUIContent(Styles.fail) {tooltip = $"Failed!\n{failMessage}"}, iconOptions);
                    else if (cancelled) GUILayout.Label(Styles.cancel, iconOptions);

                    GUILayout.Label(isClean ? Styles.clean : new GUIContent(Styles.warn) {tooltip = $"{obsoleteObjects.Length} Obsolete!"}, iconOptions);
                    GUILayout.FlexibleSpace();

                    GUILayout.Label(obsoleteObjects.Length.ToString(), Styles.fadedLabel);

                    using (new EditorGUI.DisabledScope(isClean || !completed))
                        if (GUILayout.Button("Clean", Styles.toolbarButton, buttonOptions))
                            CleanUpController();

                    if (finished)
                    {
                        using (new EditorGUI.DisabledScope(!controller))
                            if (GUILayout.Button("Scan", Styles.toolbarButton, buttonOptions))
                                ScanController(controller);
                    }
                    else if (GUILayout.Button("Cancel", Styles.toolbarButton))
                        CancelScan();
                }
            }

            #region Cleanup
            public void CleanUpController()
            {
                if (!completed || obsoleteObjects.Length <= 0) return;
                StringBuilder removedAssets = new StringBuilder();

                foreach (var o in obsoleteObjects)
                {
                    if (!o) continue;
                    removedAssets.AppendLine($"{o.name} ({o.GetType().Name})");
                    AssetDatabase.RemoveObjectFromAsset(o);
                    DestroyImmediate(o);
                }

                GreenLog($"Removed {obsoleteObjects.Length} unused Sub-Assets from {controller.name}!\n\n{removedAssets}");
                RemoveMissingTransitions();
                ScanController(controller);
            }

            private void RemoveMissingTransitions(AnimatorStateMachine m)
            {
                m.entryTransitions = KeepValid(m.entryTransitions);
                m.anyStateTransitions = KeepValid(m.anyStateTransitions);
                foreach (var cs in m.states)
                {
                    cs.state.transitions = KeepValid(cs.state.transitions);
                    EditorUtility.SetDirty(cs.state);
                }
                foreach (var cssm in m.stateMachines)
                {
                    m.SetStateMachineTransitions(cssm.stateMachine, KeepValid(m.GetStateMachineTransitions(cssm.stateMachine)));
                    RemoveMissingTransitions(cssm.stateMachine);
                }
                EditorUtility.SetDirty(m);
            }

            private void RemoveMissingTransitions()
            {
                foreach (var l in controller.layers)
                    RemoveMissingTransitions(l.stateMachine);
            }
            #endregion

            #region Scanning
            private void CancelScan() => tokenSource?.Cancel();

            private void ScanController(AnimatorController target)
            {
                controller = target;
                allObjects = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller))
                    .Where(o => o && scanTypes.Contains(o.GetType())).ToArray();
                AsyncScanControllerStart();
            }

            private async void AsyncScanControllerStart()
            {
                stopwatch.Restart();
                tokenSource = new CancellationTokenSource();
                usedObjects = new ConcurrentBag<Object>();
                obsoleteObjects = Array.Empty<Object>();
                finished = finalizing = failed = completed = cancelled = false;
                await AsyncScanController();
                stopwatch.Stop();
                finalizing = true;
            }
            private async Task AsyncScanController()
            {
                Task[] tasks = new Task[controller.layers.Length];
                try
                {
                    for (int i = 0; i < tasks.Length; i++)
                        tasks[i] = AsyncScanMachine(controller.layers[i].stateMachine, true);
                }
                catch (OperationCanceledException)
                {
                    cancelled = finished = true;
                    stopwatch.Stop();
                }
                catch (Exception e)
                {
                    failed = finished = true;
                    failMessage = e.ToString();
                    stopwatch.Stop();
                }
                await Task.WhenAll(tasks);
            }
            private async Task AsyncScanMachine(AnimatorStateMachine machine, bool isRootMachine)
            {
                try
                {
                    tokenSource.Token.ThrowIfCancellationRequested();

                    usedObjects.Add(machine);
                    AddBehaviours(machine.behaviours);
                    foreach (var cs in machine.states)
                    {
                        var s = cs.state;
                        usedObjects.Add(s);
                        AddTree(s.motion as BlendTree);
                        AddTransitions(s.transitions);
                        AddBehaviours(s.behaviours);
                    }

                    AddTransitions(machine.entryTransitions);
                    if (isRootMachine) AddTransitions(machine.anyStateTransitions);

                    Task[] tasks = new Task[machine.stateMachines.Length];
                    for (int i = 0; i < tasks.Length; i++)
                        tasks[i] = AsyncScanMachine(machine.stateMachines[i].stateMachine, false);
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                    cancelled = finished = true;
                    stopwatch.Stop();
                }
                catch (Exception e)
                {
                    failed = finished = true;
                    failMessage = e.ToString();
                    stopwatch.Stop();
                }
            }
            private void FinalizeScan()
            {
                stopwatch.Start();

                foreach (var l in controller.layers) 
                    FinalScanMachine(l.stateMachine);

                obsoleteObjects = allObjects.Except(usedObjects).ToArray();
                stopwatch.Stop();
                finished = completed = true;
            }
            private void FinalScanMachine(AnimatorStateMachine machine)
            {
                if (!machine) return;
                try
                {
                    foreach (var cssm in machine.stateMachines)
                    {
                        if (!cssm.stateMachine) continue;
                        AddTransitions(machine.GetStateMachineTransitions(cssm.stateMachine));
                        FinalScanMachine(cssm.stateMachine);
                    }
                }
                catch (Exception e)
                {
                    failed = finished = true;
                    failMessage = e.ToString();
                    stopwatch.Stop();
                }
            }
            #endregion

            #region Helpers
            private void AddTransitions(IEnumerable<AnimatorTransitionBase> transitions)
            {
                foreach (var t in transitions)
                {
                    if (!t) continue;
                    if (t.destinationState || t.destinationStateMachine || t.isExit)
                        usedObjects.Add(t);
                }
            }

            private void AddBehaviours(IEnumerable<StateMachineBehaviour> behaviours)
            {
                foreach (var b in behaviours)
                    usedObjects.Add(b);
            }
            private void AddTree(BlendTree t)
            {
                if (!t) return;
                usedObjects.Add(t);
                foreach (var cm in t.children)
                    AddTree(cm.motion as BlendTree);
            }

            private static T[] KeepValid<T>(T[] array) where T : Object
                => array.Where(o => o).ToArray();
            #endregion
        }

        private static class Styles
        {
            public static readonly GUIContent warn = EditorGUIUtility.IconContent("console.warnicon");
            public static readonly GUIContent cancel = new GUIContent(EditorGUIUtility.IconContent("console.warnicon.inactive.sml")) {tooltip = "Skipped"};
            public static readonly GUIContent clean = new GUIContent(EditorGUIUtility.IconContent("TestPassed")) { tooltip = "Clean!" };
            public static readonly GUIContent fail = new GUIContent(EditorGUIUtility.IconContent("console.erroricon.sml")) {tooltip = "Failed"};

            public static readonly GUIStyle fadedLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel);
            public static readonly GUIStyle toolbarButton = GUI.skin.GetStyle("toolbarbutton");
        }
    }
}
