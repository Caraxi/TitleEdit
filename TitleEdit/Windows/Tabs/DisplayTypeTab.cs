using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using TitleEdit.Data.Persistence;
using TitleEdit.Utility;

namespace TitleEdit.Windows.Tabs
{
    internal class DisplayTypeTab : AbstractTab
    {
        public override string Title => "Display";

        private ulong currentContentId = 0;

        public override void Draw()
        {
            base.Draw();
            ImGui.TextUnformatted("Title screen");
            DrawSelectPresetCombo("Title screen setting", Services.ConfigurationService.TitleDisplayTypeOption, (result) => Services.ConfigurationService.TitleDisplayTypeOption = result);
            GuiUtils.Combo("Default title screen logo", ref Services.ConfigurationService.TitleScreenLogo, filter: (entry) => entry != TitleScreenLogo.Unspecified);
            ImGui.Checkbox("Override preset title screen logo setting", ref Services.ConfigurationService.OverridePresetTitleScreenLogo);

            ImGui.Separator();
            ImGui.TextUnformatted("Character select screen");
            DrawSelectPresetCombo("Nothing selected setting", Services.ConfigurationService.NoCharacterDisplayType, (result) => Services.ConfigurationService.NoCharacterDisplayType = result, false);
            ImGuiComponents.HelpMarker("Preset that is used when no character is selected");
            DrawSelectPresetCombo("Global character setting", Services.ConfigurationService.GlobalDisplayType, (result) => Services.ConfigurationService.GlobalDisplayType = result);

            ImGui.Separator();
            ImGui.TextUnformatted("Character overrides");
            var usedIds = new HashSet<ulong>(Services.ConfigurationService.DisplayTypeOverrides.Count);
            var tableHeight = MathF.Max(1f, MathF.Min(8f, Services.ConfigurationService.DisplayTypeOverrides.Count) * (ImGui.CalcTextSize(" ").Y + ImGui.GetStyle().CellPadding.Y * 2 + ImGui.GetStyle().FramePadding.Y * 2));

            using (ImRaii.Table($"Display Type Overrides##{Title}", 4, ImGuiTableFlags.ScrollY, new(0, tableHeight)))
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                //Empty column for setting scroll bar spacing
                ImGui.TableSetupColumn("scrollbar_spacing", ImGuiTableColumnFlags.WidthFixed, ImGui.GetStyle().ScrollbarSize);
                for (int i = 0; i < Services.ConfigurationService.DisplayTypeOverrides.Count; i++)
                {
                    var entry = Services.ConfigurationService.DisplayTypeOverrides[i];
                    usedIds.Add(entry.Key);
                    ImGui.TableNextColumn();
                    var label = Services.CharactersService.Characters.TryGetValue(entry.Key, out var charName) ? charName : entry.Key.ToString("X");
                    ImGui.TextUnformatted(label);
                    ImGui.TableNextColumn();

                    ImGui.SetNextItemWidth(16f * ImGui.GetFontSize());
                    DrawSelectPresetCombo($"##Character override##{i}", entry.Value, (result) => Services.ConfigurationService.DisplayTypeOverrides[i] = new(entry.Key, result));
                    ImGui.TableNextColumn();
                    if (ImGuiComponents.IconButton($"##{Title}##{i}##Delete", FontAwesomeIcon.Trash))
                    {
                        Services.ConfigurationService.DisplayTypeOverrides.RemoveRange(i, 1);
                        Services.ConfigurationService.Save();
                        i--;
                    }
                    ImGui.TableNextColumn();
                }
            }

            var characterLabel = Services.CharactersService.Characters.TryGetValue(currentContentId, out var characterName) ? characterName : "";
            GuiUtils.FilterCombo("##Character override", characterLabel, () =>
            {
                bool empty = true;
                foreach (var entry in Services.CharactersService.Characters)
                {
                    if (usedIds.Contains(entry.Key)) continue;
                    empty = false;
                    if (GuiUtils.FilterSelectable($"{entry.Value}##{Title}##Character override##{entry.Key}", entry.Key == currentContentId))
                    {
                        currentContentId = entry.Key;
                        return true;
                    }
                }
                if (empty)
                {
                    ImGui.TextUnformatted("All characters configured");
                }
                return false;
            });
            ImGuiComponents.HelpMarker("These characters are collected from the character select screen.\nIf any are missing go through all the worlds with characters created.");
            ImGui.SameLine();
            ImGui.BeginDisabled(currentContentId == 0);
            if (ImGuiComponents.IconButton($"##{Title}##New", FontAwesomeIcon.Plus))
            {

                Services.ConfigurationService.DisplayTypeOverrides.Add(new(currentContentId, new() { Type = CharacterDisplayType.LastLocation }));
                Services.ConfigurationService.Save();

                currentContentId = 0;
            }
            ImGui.EndDisabled();
        }

        private void DrawSelectPresetCombo(string label, CharacterDisplayTypeOption value, Action<CharacterDisplayTypeOption> selectAction, bool showLastLocationOption = true)
        {
            string display;
            bool invalid = false;
            if (value.Type == CharacterDisplayType.LastLocation)
            {
                display = "Last location";
            }
            else if (value.Type == CharacterDisplayType.Preset)
            {
                if (value.PresetPath != null && Services.PresetService.TryGetPreset(value.PresetPath, out var preset, LocationType.CharacterSelect))
                {

                    display = preset.Name;
                }
                else
                {
                    display = "Invalid preset";
                    invalid = true;

                }
            }
            else if (value.Type == CharacterDisplayType.Random)
            {
                if (value.PresetPath != null && Services.GroupService.TryGetGroup(value.PresetPath, out var group, LocationType.CharacterSelect))
                {
                    display = $"[G] {group.Name}";
                }
                else
                {
                    {
                        display = "Invalid group";
                        invalid = true;
                    }
                }
            }
            else
            {
                display = "Something went wrong";
                invalid = true;
            }

            if (invalid)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.8f, 0, 1));
            }
            GuiUtils.FilterCombo(label, display, () =>
            {
                if (showLastLocationOption)
                {
                    if (GuiUtils.FilterSelectable($"Last location##{Title}##{label}##last location", value.Type == CharacterDisplayType.LastLocation))
                    {
                        selectAction.Invoke(new()
                        {
                            Type = CharacterDisplayType.LastLocation
                        });
                        Services.ConfigurationService.Save();
                        return true;
                    }
                    GuiUtils.DrawDisplayTypeTooltip(CharacterDisplayType.LastLocation);

                }
                GuiUtils.FilterSeperator();


                foreach (var entry in Services.PresetService.CharacterSelectPresetEnumerator)
                {
                    if (GuiUtils.FilterSelectable($"{entry.Value.Name}##{Title}##{label}##{entry.Key}", value.Type == CharacterDisplayType.Preset && entry.Key == value.PresetPath))
                    {
                        selectAction.Invoke(new()
                        {
                            Type = CharacterDisplayType.Preset,
                            PresetPath = entry.Key
                        });
                        Services.ConfigurationService.Save();
                        return true;
                    }
                    GuiUtils.DrawDisplayTypeTooltip(CharacterDisplayType.Preset, entry.Key);
                }
                foreach (var entry in Services.GroupService.CharacterSelectPresetEnumerator)
                {
                    if (GuiUtils.FilterSelectable($"[G] {entry.Value.Name}##{Title}##{label}##{entry.Key}", value.Type == CharacterDisplayType.Random && entry.Key == value.PresetPath))
                    {
                        selectAction.Invoke(new()
                        {
                            Type = CharacterDisplayType.Random,
                            PresetPath = entry.Key
                        });
                        Services.ConfigurationService.Save();
                        return true;
                    }
                    GuiUtils.DrawDisplayTypeTooltip(CharacterDisplayType.Random, entry.Key);
                }
                return false;
            }, popStyleColor: invalid);
            GuiUtils.DrawDisplayTypeTooltip(value);
        }

        private void DrawSelectPresetCombo(string label, TitleDisplayTypeOption value, Action<TitleDisplayTypeOption> selectAction)
        {
            string display;
            bool invalid = false;
            if (value.Type == TitleDisplayType.Preset)
            {
                if (value.PresetPath != null && Services.PresetService.TryGetPreset(value.PresetPath, out var preset, LocationType.TitleScreen))
                {

                    display = preset.Name;
                }
                else
                {
                    display = "Invalid preset";
                    invalid = true;

                }
            }
            else if (value.Type == TitleDisplayType.Random)
            {
                if (value.PresetPath != null && Services.GroupService.TryGetGroup(value.PresetPath, out var group, LocationType.TitleScreen))
                {
                    display = $"[G] {group.Name}";
                }
                else
                {
                    {
                        display = "Invalid group";
                        invalid = true;
                    }
                }
            }
            else
            {
                display = "Something went wrong";
                invalid = true;
            }

            if (invalid)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.8f, 0, 1));
            }
            GuiUtils.FilterCombo(label, display, () =>
            {
                foreach (var entry in Services.PresetService.TitleScreenPresetEnumerator)
                {
                    if (GuiUtils.FilterSelectable($"{entry.Value.Name}##{Title}##{label}##{entry.Key}", value.Type == TitleDisplayType.Preset && entry.Key == value.PresetPath))
                    {
                        selectAction.Invoke(new()
                        {
                            Type = TitleDisplayType.Preset,
                            PresetPath = entry.Key
                        });
                        Services.ConfigurationService.Save();
                        return true;
                    }
                    GuiUtils.DrawDisplayTypeTooltip(TitleDisplayType.Preset, entry.Key);

                }
                foreach (var entry in Services.GroupService.TitleScreenPresetEnumerator)
                {
                    if (GuiUtils.FilterSelectable($"[G] {entry.Value.Name}##{Title}##{label}##{entry.Key}", value.Type == TitleDisplayType.Random && entry.Key == value.PresetPath))
                    {
                        selectAction.Invoke(new()
                        {
                            Type = TitleDisplayType.Random,
                            PresetPath = entry.Key
                        });
                        Services.ConfigurationService.Save();
                        return true;
                    }
                    GuiUtils.DrawDisplayTypeTooltip(TitleDisplayType.Random, entry.Key);
                }
                return false;
            }, popStyleColor: invalid);
            GuiUtils.DrawDisplayTypeTooltip(value);
        }
    }
}
