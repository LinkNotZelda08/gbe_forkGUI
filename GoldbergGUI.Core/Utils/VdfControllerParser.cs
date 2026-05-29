using GoldbergGUI.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace GoldbergGUI.Core.Utils
{
    /// <summary>
    /// Parses a Steam controller-config VDF file into the gbe_fork
    /// controller action-set format (ActionName=BUTTON or ActionName=BUTTON=analogType).
    ///
    /// Extraction flow:
    ///   1. Tokenise VDF text → nested Dictionary tree.
    ///   2. controller_mappings.preset.group_source_bindings  →  groupId → physical source
    ///   3. controller_mappings.group[].inputs  →  (source, inputKey) + binding string
    ///      "game_action SetKey ActionName, ..."  →  SetKey → ActionName → gbe_fork button
    ///   4. controller_mappings.actions  →  action type (Button / StickPadGyro / AnalogTrigger)
    ///      + input_mode (joystick_move etc.)  →  analog suffix
    ///   5. controller_mappings.localization.english  →  human-readable action-set names.
    /// </summary>
    public static class VdfControllerParser
    {
        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        public static List<ControllerActionSet> Parse(string vdfContent)
        {
            var result = new List<ControllerActionSet>();
            try
            {
                var tokens = Tokenize(vdfContent);
                int pos = 0;
                var root = ParseObject(tokens, ref pos);

                if (!root.TryGetValue("controller_mappings", out var cmObj) ||
                    cmObj is not Dictionary<string, object> cm)
                    return result;

                // ── 1. Localization ─────────────────────────────────────────────
                var locale = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (cm.TryGetValue("localization", out var locRaw) &&
                    locRaw is Dictionary<string, object> locDict)
                {
                    var engObj = locDict.TryGetValue("english", out var e) ? e
                               : locDict.Values.FirstOrDefault();
                    if (engObj is Dictionary<string, object> eng)
                        foreach (var kv in eng)
                            if (kv.Value is string s) locale[kv.Key] = s;
                }

                // ── 2. Group-source map: groupId → physical source string ────────
                // Merge ALL presets so that groups belonging to every action set
                // (InGameControls, MenuControls, etc.) are included.
                var groupSrc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (cm.TryGetValue("preset", out var preRaw))
                {
                    var presets = preRaw is List<object> pl ? pl : new List<object> { preRaw };
                    foreach (var pre in presets.OfType<Dictionary<string, object>>())
                    {
                        if (!pre.TryGetValue("group_source_bindings", out var gsbRaw) ||
                            gsbRaw is not Dictionary<string, object> gsb) continue;
                        foreach (var kv in gsb)
                            if (kv.Value is string src)
                                groupSrc[kv.Key] = src.Split(' ')[0].ToLower();
                    }
                }

                // ── 3. Action type info: setKey → actionName → (typeKey, inputMode) ──
                var actionInfo = new Dictionary<string,
                    Dictionary<string, (string type, string mode)>>(StringComparer.OrdinalIgnoreCase);
                if (cm.TryGetValue("actions", out var actsRaw) &&
                    actsRaw is Dictionary<string, object> actsDict)
                {
                    foreach (var (setKey, setVal) in actsDict)
                    {
                        if (setVal is not Dictionary<string, object> setDef) continue;
                        var setMap = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

                        foreach (var (typeKey, typeVal) in setDef)
                        {
                            if (typeKey.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                                typeKey.Equals("legacy_set", StringComparison.OrdinalIgnoreCase)) continue;
                            if (typeVal is not Dictionary<string, object> typeGroup) continue;

                            foreach (var (actName, actDef) in typeGroup)
                            {
                                var mode = actDef is Dictionary<string, object> ad &&
                                           ad.TryGetValue("input_mode", out var im)
                                    ? im as string ?? "" : "";
                                setMap[actName] = (typeKey, mode);
                            }
                        }
                        actionInfo[setKey] = setMap;
                    }
                }

                // ── 4. Walk groups → collect (setKey, actionName) → gbe_button ──
                var bindings = new Dictionary<string,
                    Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

                if (cm.TryGetValue("group", out var grpRaw))
                {
                    var groups = grpRaw is List<object> gl ? gl : new List<object> { grpRaw };
                    foreach (var grpObj in groups.OfType<Dictionary<string, object>>())
                    {
                        if (!grpObj.TryGetValue("id", out var idRaw) || idRaw is not string groupId) continue;
                        if (!groupSrc.TryGetValue(groupId, out var src)) continue;

                        // Process per-input digital bindings (may be absent or empty for analog groups)
                        if (grpObj.TryGetValue("inputs", out var inpRaw) &&
                            inpRaw is Dictionary<string, object> inputs)
                        foreach (var (inputKey, inputVal) in inputs)
                        {
                            if (inputVal is not Dictionary<string, object> inp) continue;
                            var gameActions = ExtractGameActions(inp);
                            var gbeBtn = MapButton(src, inputKey.ToLower());
                            if (gbeBtn == null) continue;

                            foreach (var (setKey, actName) in gameActions)
                            {
                                if (!bindings.TryGetValue(setKey, out var sb))
                                    bindings[setKey] = sb = new Dictionary<string, string>(
                                        StringComparer.OrdinalIgnoreCase);

                                if (sb.TryGetValue(actName, out var existing))
                                {
                                    // Avoid duplicates that arise from multiple activator types
                                    // (e.g., Full_Press + Release both binding the same game_action).
                                    var parts = existing.Split(',');
                                    if (!parts.Any(p => p.Equals(gbeBtn, StringComparison.OrdinalIgnoreCase)))
                                        sb[actName] = existing + "," + gbeBtn;
                                }
                                else
                                    sb[actName] = gbeBtn;
                            }
                        }

                        // ── Group-level "gameactions" (analog/trigger groups with empty inputs) ──
                        // Format: "gameactions" { "SetKey" "ActionName" ... }
                        // The button is derived from the group's physical source, not from an input key.
                        if (grpObj.TryGetValue("gameactions", out var gaRaw) &&
                            gaRaw is Dictionary<string, object> grpGameActions)
                        {
                            var analogBtn = src switch
                            {
                                "joystick" or "left_joystick" => "LJOY",
                                "right_joystick"              => "RJOY",
                                "left_trigger"                => "LTRIGGER",
                                "right_trigger"               => "RTRIGGER",
                                _                             => null
                            };

                            if (analogBtn != null)
                            {
                                foreach (var (setKey, actNameObj) in grpGameActions)
                                {
                                    if (actNameObj is not string actName ||
                                        string.IsNullOrEmpty(actName)) continue;

                                    if (!bindings.TryGetValue(setKey, out var sb))
                                        bindings[setKey] = sb = new Dictionary<string, string>(
                                            StringComparer.OrdinalIgnoreCase);

                                    if (!sb.ContainsKey(actName))
                                        sb[actName] = analogBtn;
                                }
                            }
                        }
                    }
                }

                // ── 5. Build result ──────────────────────────────────────────────
                foreach (var (setKey, setBindings) in bindings)
                {
                    // Resolve human-readable name via localization ("Set_Default" → "InGameControls")
                    var locKey = "Set_" + setKey;
                    var name = locale.TryGetValue(locKey, out var ln) ? ln : setKey;

                    var aset = new ControllerActionSet { Name = name };

                    foreach (var (actName, rawBtn) in setBindings)
                    {
                        var finalBtn = rawBtn;

                        // Append analog suffix from action type info
                        if (actionInfo.TryGetValue(setKey, out var si) &&
                            si.TryGetValue(actName, out var ti))
                        {
                            if (ti.type.Equals("StickPadGyro", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(ti.mode))
                                finalBtn += "=" + ti.mode;
                            else if (ti.type.Equals("AnalogTrigger", StringComparison.OrdinalIgnoreCase))
                                finalBtn += "=trigger";
                        }

                        aset.Bindings.Add(new ControllerBinding { ActionName = actName, Binding = finalBtn });
                    }

                    if (aset.Bindings.Count > 0)
                        result.Add(aset);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VdfControllerParser] {ex.Message}");
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // Extract "game_action <SetKey> <ActionName>" binding strings from one
        // input definition's activator tree.
        // -----------------------------------------------------------------------
        private static List<(string set, string action)> ExtractGameActions(
            Dictionary<string, object> inputDef)
        {
            var result = new List<(string, string)>();
            if (!inputDef.TryGetValue("activators", out var actRaw) ||
                actRaw is not Dictionary<string, object> activators) return result;

            foreach (var activatorVal in activators.Values)
            {
                if (activatorVal is not Dictionary<string, object> act) continue;
                if (!act.TryGetValue("bindings", out var bRaw) ||
                    bRaw is not Dictionary<string, object> bDict) continue;

                // Multiple "binding" values are stored as List<object> by the parser
                var bindVal = bDict.TryGetValue("binding", out var bv) ? bv : null;
                var bindStrings = bindVal is List<object> bl
                    ? bl.OfType<string>().ToList()
                    : bindVal is string bs ? new List<string> { bs } : new List<string>();

                foreach (var bindStr in bindStrings)
                {
                    if (!bindStr.StartsWith("game_action ", StringComparison.OrdinalIgnoreCase)) continue;
                    var rest = bindStr["game_action ".Length..].Split(',')[0].Trim();
                    var parts = rest.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                        result.Add((parts[0], parts[1]));
                }
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // Map (physical source, VDF input key) → gbe_fork button name.
        // The analog suffix (=joystick_move, =trigger) is NOT added here;
        // it is applied later from the actions section.
        // -----------------------------------------------------------------------
        private static string MapButton(string source, string inputKey)
        {
            return source switch
            {
                "face_buttons" or "button_diamond" => inputKey switch
                {
                    "button_a" => "A",
                    "button_b" => "B",
                    "button_x" => "X",
                    "button_y" => "Y",
                    _ => null
                },
                "switch" => inputKey switch
                {
                    "button_escape"                    => "START",
                    "button_menu" or "button_back_left" => "BACK",
                    "left_bumper"                      => "LBUMPER",
                    "right_bumper"                     => "RBUMPER",
                    _                                  => null
                },
                "left_trigger" => inputKey.Contains("click") ? "DLTRIGGER" : "LTRIGGER",
                "right_trigger" => inputKey.Contains("click") ? "DRTRIGGER" : "RTRIGGER",
                "joystick" or "left_joystick" => inputKey switch
                {
                    "left_stick" => "LJOY",
                    "click" => "LSTICK",
                    "dpad_north" => "DLJOYUP",
                    "dpad_south" => "DLJOYDOWN",
                    "dpad_west" => "DLJOYLEFT",
                    "dpad_east" => "DLJOYRIGHT",
                    _ => null
                },
                "right_joystick" => inputKey switch
                {
                    "right_joystick" or "right_stick" => "RJOY",
                    "click" => "RSTICK",
                    "dpad_north" => "DRJOYUP",
                    "dpad_south" => "DRJOYDOWN",
                    "dpad_west" => "DRJOYLEFT",
                    "dpad_east" => "DRJOYRIGHT",
                    _ => null
                },
                "dpad" => inputKey switch
                {
                    "dpad_north" => "DUP",
                    "dpad_south" => "DDOWN",
                    "dpad_west" => "DLEFT",
                    "dpad_east" => "DRIGHT",
                    _ => null
                },
                "left_bumper" => "LBUMPER",
                "right_bumper" => "RBUMPER",
                "button_escape" or "start" => "START",
                "button_back_left" or "back" or "button_back" or "select" => "BACK",
                _ => null
            };
        }

        // -----------------------------------------------------------------------
        // VDF Tokenizer
        // -----------------------------------------------------------------------
        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '{' || c == '}') { tokens.Add(c.ToString()); i++; }
                else if (c == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { i++; sb.Append(text[i]); }
                        else sb.Append(text[i]);
                        i++;
                    }
                    if (i < text.Length) i++; // skip closing "
                    tokens.Add(sb.ToString());
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                { while (i < text.Length && text[i] != '\n') i++; }
                else if (!char.IsWhiteSpace(c))
                {
                    // Bare (unquoted) token — rare in VDF but handle it
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) &&
                           text[i] != '{' && text[i] != '}' && text[i] != '"') i++;
                    tokens.Add(text[start..i]);
                }
                else i++;
            }
            return tokens;
        }

        // -----------------------------------------------------------------------
        // VDF recursive-descent parser  →  Dictionary<string, object>
        // Duplicate keys are accumulated into List<object>.
        // -----------------------------------------------------------------------
        private static Dictionary<string, object> ParseObject(List<string> t, ref int pos)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (pos < t.Count && t[pos] != "}")
            {
                if (t[pos] == "{") { pos++; continue; } // unexpected opening brace
                var key = t[pos++];
                if (pos >= t.Count) break;

                object val;
                if (t[pos] == "{")
                {
                    pos++;
                    val = ParseObject(t, ref pos);
                    if (pos < t.Count && t[pos] == "}") pos++;
                }
                else
                {
                    val = t[pos++];
                }

                if (d.TryGetValue(key, out var existing))
                {
                    if (existing is List<object> list) list.Add(val);
                    else d[key] = new List<object> { existing, val };
                }
                else d[key] = val;
            }
            return d;
        }
    }
}
