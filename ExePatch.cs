﻿using System;
using System.Diagnostics;
using Reloaded.Mod.Interfaces;
using System.IO;
using System.Linq;
using p4gpc.inaba.Configuration;
using System.Collections.Generic;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using System.Text.RegularExpressions;
using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using System.Drawing;
using Reloaded.Memory.Sigscan.Definitions.Structs;
using System.Globalization;
using Reloaded.Memory.Sigscan;
using Reloaded.Memory;
using Reloaded.Memory.Interfaces;

namespace p4gpc.inaba
{
    public class ExePatch : IDisposable
    {
        private readonly Memory mem;
        private readonly ILogger mLogger;
        private readonly IStartupScanner mStartupScanner;
        private readonly Config mConfig;

        private readonly Process mProc;
        private readonly nuint mBaseAddr;
        private IReloadedHooks mHooks;
        private List<IAsmHook> mAsmHooks;

        private CultureInfo culture = new CultureInfo("en-AU"); // I'm Australian, deal with it

        public ExePatch(ILogger logger, IStartupScanner startupScanner, Config config, IReloadedHooks hooks)
        {
            mLogger = logger;
            mConfig = config;
            mStartupScanner = startupScanner;
            mHooks = hooks;
            mProc = Process.GetCurrentProcess();
            mBaseAddr = (nuint)(nint)mProc.MainModule.BaseAddress;
            mem = new Memory();
            mAsmHooks = new List<IAsmHook>();
        }

        private void SinglePatch(string filePath)
        {
            byte[] file;
            uint fileLen;
            string fileName = Path.GetFileName(filePath);

            file = File.ReadAllBytes(filePath);
            mLogger.WriteLine($"[Inaba Exe Patcher] Loading {fileName}");

            if (file.Length < 2)
            {
                mLogger.WriteLine("[Inaba Exe Patcher] Improper .patch format.");
                return;
            }

            // Length of line to search is reversed in hex.
            byte[] fileHeaderLenBytes = file[0..2];
            Array.Reverse(fileHeaderLenBytes, 0, 2);
            int fileHeaderLen = BitConverter.ToInt16(fileHeaderLenBytes);

            if (file.Length < 2 + fileHeaderLen)
            {
                mLogger.WriteLine("[Inaba Exe Patcher] Improper .patch format.");
                return;
            }

            // Header is line to search for in exe
            byte[] fileHeader = file[2..(fileHeaderLen + 2)];
            // Contents is what to replace
            byte[] fileContents = file[(fileHeaderLen + 2)..];
            fileLen = Convert.ToUInt32(fileContents.Length);

            // Debug
            if (mConfig.Debug)
            {
                mLogger.WriteLine($"[Inaba Exe Patcher] (Debug) Length of Search Pattern = {fileHeaderLen}");
                mLogger.WriteLine($"[Inaba Exe Patcher] (Debug) Search Pattern (in hex) = {BitConverter.ToString(fileHeader).Replace("-", " ")}");
                mLogger.WriteLine($"[Inaba Exe Patcher] (Debug) Replacement Content (in hex) = {BitConverter.ToString(fileContents).Replace("-", " ")}");
            }
            var pattern = BitConverter.ToString(fileHeader).Replace("-", " ");
            mStartupScanner.AddMainModuleScan(pattern,
                (result) =>
                {
                    if (result.Found)
                    {
                        mem.SafeWrite(mBaseAddr + (nuint)result.Offset, fileContents);
                        mLogger.WriteLine($"[Inaba Exe Patcher] Successfully found and overwrote pattern in {fileName}");
                    }
                    else
                        mLogger.WriteLine($"[Inaba Exe Patcher] Couldn't find pattern to replace using {fileName}");
                });
        }

        public void Patch(string patchPath)
        {
            List<string> patchPriorityList = new List<string>();
            // Add main directory as last entry for least priority
            patchPriorityList.Add(patchPath);

            // Add every other directory
            foreach (var dir in Directory.EnumerateDirectories(patchPath))
            {
                var name = Path.GetFileName(dir);

                patchPriorityList.Add($@"{patchPath}{Path.DirectorySeparatorChar}{name}");
            }

            // Reverse order of config patch list so that the higher priorities are moved to the end
            List<string> revEnabledPatches = mConfig.PatchFolderPriority;
            revEnabledPatches.Reverse();

            foreach (var dir in revEnabledPatches)
            {
                var name = Path.GetFileName(dir);
                if (patchPriorityList.Contains($@"{patchPath}{Path.DirectorySeparatorChar}{name}", StringComparer.InvariantCultureIgnoreCase))
                {
                    patchPriorityList.Remove($@"{patchPath}{Path.DirectorySeparatorChar}{name}");
                    patchPriorityList.Add($@"{patchPath}{Path.DirectorySeparatorChar}{name}");
                }
            }

            // Load EnabledPatches in order
            foreach (string dir in patchPriorityList)
            {
                string[] allFiles = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);

                string[] patches = allFiles.Where(s => Path.GetExtension(s).ToLower() == ".patch").ToArray();
                string[] exPatches = allFiles.Where(s => Path.GetExtension(s).ToLower() == ".expatch").ToArray();

                if (patches.Length == 0 && exPatches.Length == 0)
                {
                    mLogger.WriteLine($"[Inaba Exe Patcher] No patches found in {dir}");
                    return;
                }

                mLogger.WriteLine($"[Inaba Exe Patcher] Found patches in {dir}");
                foreach (string f in patches)
                {
                    SinglePatch(f);
                }

                foreach (string f in exPatches)
                {
                    SinglePatchEx(f);
                }
            }
        }

        private void SinglePatchEx(string filePath)
        {
            (List<ExPatch> patches, Dictionary<string, string> constants) = ParseExPatch(filePath);
            foreach (var constant in constants)
            {
                mStartupScanner.AddMainModuleScan(constant.Value, result =>
                {
                    if (!result.Found)
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Couldn't find address for const {constant.Value}, it will not be replaced", Color.Red);
                        return;
                    }

                    FillInConstant(patches, constant.Key, ((nuint)result.Offset + mBaseAddr).ToString());
                });
            }
            foreach (var patch in patches)
            {
                mStartupScanner.AddMainModuleScan(patch.Pattern, (result) =>
                {

                    if (!result.Found)
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Couldn't find address for {patch.Name}, not applying it", Color.Red);
                        return;
                    }

                    FillInPatchConstants(patch, result);

                    if (patch.IsReplacement)
                        ReplacementPatch(patch, result, filePath);
                    else
                        FunctionPatch(patch, result, filePath);
                });
            }
        }

        /// <summary>
        /// Fill in constants that require the address of the patch (patchAddress, call, and jump)
        /// </summary>
        /// <param name="patch">The patch to fill constants in</param>
        /// <param name="result">The result of a signature scan for the patch</param>
        private void FillInPatchConstants(ExPatch patch, PatternScanResult result)
        {
            for (int i = 0; i < patch.Function.Length; i++)
            {
                patch.Function[i] = patch.Function[i].Replace("{patchAddress}", ((nuint)result.Offset + mBaseAddr).ToString());
                patch.Function[i] = patch.Function[i].Replace("{replacementAddress}", ((nuint)result.Offset + mBaseAddr).ToString());

                var callMatch = Regex.Match(patch.Function[i], @"{call\s+(.+)}", RegexOptions.IgnoreCase);
                if (callMatch.Success)
                {
                    if (!Utils.EvaluateExpression(callMatch.Groups[1].Value, out var address))
                        mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse address {callMatch.Groups[1].Value} for call instruction");
                    else
                        patch.Function[i] = patch.Function[i].Replace(callMatch.Groups[0].Value, mHooks.Utilities.GetAbsoluteCallMnemonics((nint)address, Environment.Is64BitProcess));
                }

                var jumpMatch = Regex.Match(patch.Function[i], @"{(?:jump|jmp)\s+(.+)}", RegexOptions.IgnoreCase);
                if (jumpMatch.Success)
                {
                    if (!Utils.EvaluateExpression(jumpMatch.Groups[1].Value, out var address))
                        mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse address {jumpMatch.Groups[1].Value} for jump instruction");
                    else
                    {
                        patch.Function[i] = patch.Function[i].Replace(jumpMatch.Groups[0].Value, mHooks.Utilities.GetAbsoluteJumpMnemonics((nint)address, Environment.Is64BitProcess));
                    }
                }

            }
        }

        private void FunctionPatch(ExPatch patch, PatternScanResult result, string filePath, int index = 1, int memOffset = 0)
        {
            bool isCurrentIndex = patch.Indices.Count > 0 && patch.Indices.First() == index;
            if (patch.Indices.Count == 0 || patch.AllIndices || isCurrentIndex)
            {
                AsmHookBehaviour? order = null;
                if (patch.ExecutionOrder == "before")
                {
                    order = AsmHookBehaviour.ExecuteFirst;
                    mLogger.WriteLine($"[Inaba Exe Patcher] Executing {patch.Name} function before original");
                }
                else if (patch.ExecutionOrder == "after")
                {
                    mLogger.WriteLine($"[Inaba Exe Patcher] Executing {patch.Name} function after original");
                    order = AsmHookBehaviour.ExecuteAfter;
                }
                else if (patch.ExecutionOrder == "only" || patch.ExecutionOrder == "")
                {
                    mLogger.WriteLine($"[Inaba Exe Patcher] Replacing original {patch.Name} function");
                    order = AsmHookBehaviour.DoNotExecuteOriginal;
                }
                else
                {
                    mLogger.WriteLine($"[Inaba Exe Patcher] Unknown execution order {patch.ExecutionOrder}, using default (only). Valid orders are before, after and only");
                    order = AsmHookBehaviour.DoNotExecuteOriginal;
                }

                try
                {
                    mAsmHooks.Add(mHooks.CreateAsmHook(patch.Function, (long)mBaseAddr + result.Offset + patch.Offset, (AsmHookBehaviour)order).Activate());
                }
                catch (Exception e)
                {
                    mLogger.WriteLine($"[Inaba Exe Patcher] Error while applying {patch.Name} patch: {e.Message}", Color.Red);
                    mLogger.WriteLine($"[Inaba Exe Patcher] Function dump: \n{string.Join("\n", patch.Function)}", Color.Red);
                    return;
                }
                mLogger.WriteLine($"[Inaba Exe Patcher] Applied patch {patch.Name} from {Path.GetFileName(filePath)} at 0x{(nuint)mBaseAddr + (nuint)result.Offset + (nuint)patch.Offset:X}");
            }

            if (isCurrentIndex)
                patch.Indices.RemoveAt(0);

            if (patch.Indices.Count > 0 || patch.AllIndices)
            {
                memOffset = result.Offset + patch.Pattern.Replace(" ", "").Length / 2;
                result = ScanPattern(patch.Pattern, memOffset);
                if (result.Found)
                {
                    ReplacementPatch(patch, result, filePath, index + 1, memOffset);
                }
            }
        }

        private void ReplacementPatch(ExPatch patch, PatternScanResult result, string filePath, int index = 1, int memOffset = 0)
        {
            if (patch.Function.Count() == 0)
            {
                mLogger.WriteLine($"[Inaba Exe Patcher] No replacement value specified for {patch.Name} replacement, skipping it");
                return;
            }
            bool isCurrentIndex = patch.Indices.Count > 0 && patch.Indices.First() == index;
            if (patch.Indices.Count == 0 || patch.AllIndices || isCurrentIndex)
            {
                string replacement = patch.Function.Last();
                if (patch.Function.Count() > 1)
                    mLogger.WriteLine($"[Inaba Exe Patcher] Multiple replacement values specified for {patch.Name}, using last defined one ({replacement})");
                int replacementLength = 0;
                if (patch.PadNull)
                    replacementLength = patch.Pattern.Replace(" ", "").Length / 2;
                WriteValue(replacement, mBaseAddr + (nuint)result.Offset + (nuint)patch.Offset, patch.Name, replacementLength, patch.EncodingSetting);
                mLogger.WriteLine($"[Inaba Exe Patcher] Applied replacement {patch.Name} from {Path.GetFileName(filePath)} at 0x{mBaseAddr + (nuint)result.Offset + (nuint)patch.Offset:X}");
            }
            
            if(isCurrentIndex)
                patch.Indices.RemoveAt(0);

            if (patch.Indices.Count > 0 || patch.AllIndices)
            {
                memOffset = result.Offset + patch.Pattern.Replace(" ", "").Length / 2;
                result = ScanPattern(patch.Pattern, memOffset);
                if (result.Found)
                {
                    ReplacementPatch(patch, result, filePath, index + 1, memOffset);
                }
            }

        }

        private PatternScanResult ScanPattern(string pattern, int offset)
        {
            using var thisProcess = Process.GetCurrentProcess();
            using var scanner = new Scanner(thisProcess, thisProcess.MainModule);
            return scanner.FindPattern(pattern, offset);

        }

        /// <summary>
        /// Parses all of the patches from an expatch file
        /// </summary>
        /// <param name="filePath">The path to the expatch file</param>
        /// <returns>A tuple containing a list of all of the found patches in the file and a dictionary of all constants that need to be scanned for</returns>
        private (List<ExPatch>, Dictionary<string, string>) ParseExPatch(string filePath)
        {
            List<ExPatch> patches = new List<ExPatch>();
            bool startPatch = false;
            bool startReplacement = false;
            List<string> currentPatch = new List<string>();
            string patchName = "";
            string pattern = "";
            string order = "";
            int offset = 0;
            bool padNull = true;
            List<int> indices = new();
            bool allIndices = false;
            Encoding encoding = Encoding.ASCII;
            Dictionary<string, nuint> variables = new();
            Dictionary<string, string> constants = new();
            Dictionary<string, string> scanConstants = new();

            foreach (var rawLine in File.ReadLines(filePath))
            {
                // Search for the start of a new patch (and its name)
                string line = RemoveComments(rawLine).Trim();
                var patchMatch = Regex.Match(line, @"\[\s*patch\s*(?:\s+(.*?))?\s*\]", RegexOptions.IgnoreCase);
                if (patchMatch.Success)
                {
                    SaveCurrentPatch(currentPatch, patches, patchName, ref pattern, ref order, ref offset, ref padNull, ref allIndices, indices, startReplacement, ref encoding);
                    startReplacement = false;
                    startPatch = true;
                    if (patchMatch.Groups.Count > 1)
                        patchName = patchMatch.Groups[1].Value;
                    else
                        patchName = "";
                    continue;
                }

                // Search for the start of a new replacement
                var replacementMatch = Regex.Match(line, @"\[\s*replacement\s*(?:\s+(.*?))?\s*\]", RegexOptions.IgnoreCase);
                if (replacementMatch.Success)
                {
                    SaveCurrentPatch(currentPatch, patches, patchName, ref pattern, ref order, ref offset, ref padNull, ref allIndices, indices, startReplacement, ref encoding);
                    startReplacement = true;
                    startPatch = false;
                    if (replacementMatch.Groups.Count > 1)
                        patchName = replacementMatch.Groups[1].Value;
                    else
                        patchName = "";
                    continue;
                }

                // Search for a variable definition
                var variableMatch = Regex.Match(line, @"^\s*var\s+([^\s(]+)(?:\(([0-9]+)\))?\s*(?:=\s*(.*))?", RegexOptions.IgnoreCase);
                if (variableMatch.Success)
                {
                    string name = variableMatch.Groups[1].Value;
                    if (variables.ContainsKey(name))
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Variable {name} in {Path.GetFileName(filePath)} already exists, ignoring duplicate declaration of it");
                        continue;
                    }
                    int length = 4;
                    if (variableMatch.Groups[2].Success)
                    {
                        if (!int.TryParse(variableMatch.Groups[2].Value, NumberStyles.Number, culture, out length))
                            mLogger.WriteLine($"[Inaba Exe Patcher] Invalid variable length \"{variableMatch.Groups[2].Value}\" defaulting to length of 4 bytes");
                    }
                    else
                    {
                        // Automatically set the length to that of the string if none was explicitly defined
                        var match = Regex.Match(variableMatch.Groups[3].Value, "\"(.*)\"");
                        if (match.Success)
                            length = match.Groups[1].Value.Length + 1;
                    }
                    try
                    {
                        nuint variableAddress = mem.Allocate((nuint)length).Address;
                        mLogger.WriteLine($"[Inaba Exe Patcher] Allocated {length} byte{(length != 1 ? "s" : "")} for {name} at 0x{variableAddress:X}");
                        if (variableMatch.Groups[3].Success)
                            WriteValue(variableMatch.Groups[3].Value, variableAddress, name, 0, Encoding.ASCII);
                        variables.Add(name, variableAddress);
                    }
                    catch (Exception ex)
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Unable to allocate variable {name}: {ex.Message}");
                    }
                    continue;
                }

                // Search for a constant definition 
                var constantMatch = Regex.Match(line, @"^\s*const\s+(\S+)\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (constantMatch.Success)
                {
                    string name = constantMatch.Groups[1].Value;
                    if (constants.ContainsKey(name) || scanConstants.ContainsKey(name))
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Constant {name} in {Path.GetFileName(filePath)} already exists, ignoring duplicate declaration of it");
                        continue;
                    }
                    string constValue = constantMatch.Groups[2].Value;
                    var scanMatch = Regex.Match(constValue, @"scan\((.*)\)", RegexOptions.IgnoreCase);
                    if (scanMatch.Success)
                    {
                        scanConstants.Add(name, scanMatch.Groups[1].Value);
                    }
                    else
                    {
                        constants.Add(name, constantMatch.Groups[2].Value);
                    }
                    continue;
                }

                // Don't try to add stuff if the patch hasn't actually started yet
                if (!startPatch && !startReplacement) continue;

                // Search for a patten to scan for
                var patternMatch = Regex.Match(line, @"^\s*pattern\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (patternMatch.Success)
                {
                    pattern = patternMatch.Groups[1].Value.TrimEnd();
                    continue;
                }

                // Search for an order to execute the hook in
                var orderMatch = Regex.Match(line, @"^\s*order\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (orderMatch.Success)
                {
                    order = orderMatch.Groups[1].Value;
                    continue;
                }

                // Search for an offset to make the patch/replacement on
                var offsetMatch = Regex.Match(line, @"^\s*offset\s*=\s*(([+-])?(0x|0b)?([0-9A-Fa-f]+))", RegexOptions.IgnoreCase);
                if (offsetMatch.Success)
                {
                    int offsetBase = 10;
                    if (offsetMatch.Groups[3].Success)
                    {
                        if (offsetMatch.Groups[3].Value == "0b")
                            offsetBase = 2;
                        else if (offsetMatch.Groups[3].Value == "0x")
                            offsetBase = 16;
                    }
                    try
                    {
                        offset = Convert.ToInt32(offsetMatch.Groups[4].Value, offsetBase);
                        if (offsetMatch.Groups[2].Value == "-")
                            offset *= -1;
                    }
                    catch
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse offset {offsetMatch.Groups[1].Value} as an int leaving offset as 0");
                    }
                    continue;
                }

                // Search for a literal to search for
                var searchMatch = Regex.Match(line, @"^\s*search\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (searchMatch.Success)
                {
                    string value = searchMatch.Groups[1].Value;
                    if (int.TryParse(value, NumberStyles.Number, culture, out int intValue) ||
                        Regex.IsMatch(value, @"[0-9]+f") && float.TryParse(value, NumberStyles.Number, culture, out float floatValue) ||
                        double.TryParse(value, NumberStyles.Number, culture, out double doubleValue) ||
                        Regex.Match(value, "\"(.*)\"").Success)
                    {
                        pattern = value;
                    }
                    else
                    {
                        mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse {value} as an int, double, float or string not creating search pattern");
                    }
                    continue;
                }

                // Search for a replacement (the actual value to set the thing to)
                var replaceValueMatch = Regex.Match(line, @"^\s*replacement\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (replaceValueMatch.Success)
                {
                    var value = replaceValueMatch.Groups[1].Value;
                    currentPatch.Add(value);
                    continue;
                }

                var padMatch = Regex.Match(line, @"^\s*padNull\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (padMatch.Success)
                {
                    string value = padMatch.Groups[1].Value;
                    if (!bool.TryParse(value, out padNull))
                        mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse {value} to a boolean value (true or false) leaving padNull unchanged");
                }

                var indexMatch = Regex.Match(line, @"^\s*index\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (indexMatch.Success)
                {
                    if (indexMatch.Groups[1].Value.Trim().Equals("all", StringComparison.InvariantCultureIgnoreCase))
                    {
                        allIndices = true;
                        continue;
                    }
                    foreach (var value in indexMatch.Groups[1].Value.Split(','))
                    {
                        if (!Utils.EvaluateExpression(value, out long index))
                        {
                            mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse {value} to an integer index, no index will be used");
                        }
                        else
                        {
                            indices.Add((int)index);
                        }
                    }
                    continue;
                }

                // Search for encoding method used for string. Ignored if value isn't string
                var encodingMatch = Regex.Match(line, @"^\s*encoding\s*=\s*(.+)", RegexOptions.IgnoreCase);
                if (encodingMatch.Success)
                {
                    encoding = ParseEncoding(encodingMatch.Groups[1].Value, Encoding.ASCII);
                }

                // Add the line as a part of the patch's function
                if (startPatch)
                    currentPatch.Add(line);
            }

            if (startReplacement || startPatch)
                SaveCurrentPatch(currentPatch, patches, patchName, ref pattern, ref order, ref offset, ref padNull, ref allIndices, indices, startReplacement, ref encoding);
            FillInVariables(patches, variables, constants);
            return (patches, scanConstants);
        }

        private Encoding ParseEncoding(string encodingString, Encoding defaultIfError)
        {
            try
            {
                return Encoding.GetEncoding(encodingString);
            }
            catch (ArgumentException ex)
            {
                mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse search encoding option {encodingString} as a form of text encoding." +
                    $" Should be one of {string.Join(", ", Encoding.GetEncodings().Select(current => current.Name))}." +
                    $" Defaulting to {defaultIfError.WebName}.", Color.Red);
                return defaultIfError;
            }
        }

        private void SaveCurrentPatch(List<string> currentPatch, List<ExPatch> patches, string patchName, ref string pattern, 
            ref string order, ref int offset, ref bool padNull, ref bool allIndices, List<int> indices, bool isReplacement, ref Encoding encoding)
        {
            if (currentPatch.Count > 0)
            {
                indices.Sort();
                if (!isReplacement)
                    currentPatch.Insert(0, Environment.Is64BitProcess ? "use64" : "use32");
                patches.Add(new ExPatch(patchName, pattern, currentPatch.ToArray(), order, offset, isReplacement, padNull, new List<int>(indices), allIndices, encoding, culture, mLogger));
            }
            currentPatch.Clear();
            indices.Clear();
            pattern = "";
            order = "";
            offset = 0;
            padNull = true;
            allIndices = false;
            encoding = Encoding.ASCII;
        }

        /// <summary>
        /// Replaces any constant definitions with their value
        /// </summary>
        /// <param name="patches">A list patches to replace the constant in</param>
        /// <param name="name">The name of the constant</param>
        /// <param name="value">The value of the constant</param>
        private void FillInConstant(List<ExPatch> patches, string name, string value)
        {
            foreach (var patch in patches)
            {
                for (int i = 0; i < patch.Function.Length; i++)
                {
                    patch.Function[i] = patch.Function[i].Replace($"{{{name}}}", value);
                }
            }
        }

        /// <summary>
        /// Replaces any variable and constant declarations in functions (such as {variableName}) with their actual addresses
        /// </summary>
        /// <param name="patches">A list of patches to replace the variables in</param>
        /// <param name="variables">A Dictionary where the key is the variable name and the value is the variable address</param>
        private void FillInVariables(List<ExPatch> patches, Dictionary<string, nuint> variables, Dictionary<string, string> constants)
        {
            if (variables.Count == 0 && constants.Count == 0)
                return;
            foreach (var patch in patches)
                for (int i = 0; i < patch.Function.Length; i++)
                {
                    foreach (var variable in variables)
                        patch.Function[i] = patch.Function[i].Replace($"{{{variable.Key}}}", variable.Value.ToString());
                    foreach (var constant in constants)
                        patch.Function[i] = patch.Function[i].Replace($"{{{constant.Key}}}", constant.Value);
                    patch.Function[i] = patch.Function[i].Replace("{pushCaller}", mHooks.Utilities.PushCdeclCallerSavedRegisters());
                    patch.Function[i] = patch.Function[i].Replace("{popCaller}", mHooks.Utilities.PopCdeclCallerSavedRegisters());
                    patch.Function[i] = patch.Function[i].Replace("{pushXmm}", Utils.PushXmm());
                    patch.Function[i] = patch.Function[i].Replace("{popXmm}", Utils.PopXmm());
                    var xmmMatch = Regex.Match(patch.Function[i], @"{pushXmm([0-9]+)}", RegexOptions.IgnoreCase);
                    if (xmmMatch.Success)
                        patch.Function[i] = patch.Function[i].Replace(xmmMatch.Groups[0].Value, Utils.PushXmm(int.Parse(xmmMatch.Groups[1].Value)));
                    xmmMatch = Regex.Match(patch.Function[i], @"{popXmm([0-9]+)}", RegexOptions.IgnoreCase);
                    if (xmmMatch.Success)
                        patch.Function[i] = patch.Function[i].Replace(xmmMatch.Groups[0].Value, Utils.PopXmm(int.Parse(xmmMatch.Groups[1].Value)));

                }
        }

        /// <summary>
        /// Writes the value to an address based on a string attempting to parse the string to an int, float or double, defaulting to writing it as a string if these fail
        /// </summary>
        /// <param name="value">The string to interpret and write</param>
        /// <param name="address">The address to write to</param>
        /// <param name="name">The name of the variable this is for</param>
        /// <param name="stringLength">The length of the string that should be written, if <paramref name="value"/> is shorter than this it will be padded with null characters. This has no effect if <paramref name="value"/> is not written as a string</param>
        private void WriteValue(string value, nuint address, string name, int stringLength, Encoding encodingSetting)
        {
            Match match;
            match = Regex.Match(value, @"^([+-])?(0x|0b)?([0-9A-Fa-f]+)(u)?$");
            if (match.Success)
            {
                int offsetBase = 10;
                if (match.Groups[2].Success)
                {
                    if (match.Groups[2].Value == "0b")
                        offsetBase = 2;
                    else if (match.Groups[2].Value == "0x")
                        offsetBase = 16;
                }
                try
                {
                    if (match.Groups[4].Success)
                    {
                        uint intValue = Convert.ToUInt32(match.Groups[3].Value, offsetBase);
                        mem.SafeWrite(address, BitConverter.GetBytes(intValue));
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote uint {intValue} as value of {name} at 0x{address:X}");
                    }
                    else
                    {
                        int intValue = Convert.ToInt32(match.Groups[3].Value, offsetBase);
                        if (match.Groups[1].Value == "-")
                            intValue *= -1;
                        mem.SafeWrite(address, BitConverter.GetBytes(intValue));
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote int {intValue} as value of {name} at 0x{address:X}");
                    }
                    return;
                }
                catch { }
            }
            match = Regex.Match(value, @"^([+-])?(0x|0b)?([0-9A-Fa-f]+)([su])b$");
            if (match.Success)
            {
                int offsetBase = 10;
                if (match.Groups[2].Success)
                {
                    if (match.Groups[2].Value == "0b")
                        offsetBase = 2;
                    else if (match.Groups[2].Value == "0x")
                        offsetBase = 16;
                }
                try
                {
                    if (match.Groups[4].Value == "s")
                    {
                        sbyte byteValue = Convert.ToSByte(match.Groups[3].Value, offsetBase);
                        if (match.Groups[1].Value == "-")
                            byteValue *= -1;
                        mem.SafeWrite(address, new byte[] { (byte)byteValue });
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote sbyte {byteValue} as value of {name} at 0x{address:X}");
                    }
                    else
                    {
                        byte byteValue = Convert.ToByte(match.Groups[3].Value, offsetBase);
                        mem.SafeWrite(address, new byte[] { byteValue });
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote ubyte {byteValue} as value of {name} at 0x{address:X}");
                    }
                    return;
                }
                catch { }
            }
            match = Regex.Match(value, @"^([+-])?(0x|0b)?([0-9A-Fa-f]+)(u)?l$");
            if (match.Success)
            {
                int offsetBase = 10;
                if (match.Groups[2].Success)
                {
                    if (match.Groups[2].Value == "0b")
                        offsetBase = 2;
                    else if (match.Groups[2].Value == "0x")
                        offsetBase = 16;
                }
                try
                {
                    if (match.Groups[4].Success)
                    {
                        ulong longValue = Convert.ToUInt64(match.Groups[3].Value, offsetBase);
                        mem.SafeWrite(address, BitConverter.GetBytes(longValue));
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote ulong {longValue} as value of {name} at 0x{address:X}");
                    }
                    else
                    {
                        long longValue = Convert.ToInt64(match.Groups[3].Value, offsetBase);
                        if (match.Groups[1].Value == "-")
                            longValue *= -1;
                        mem.SafeWrite(address, BitConverter.GetBytes(longValue));
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote long {longValue} as value of {name} at 0x{address:X}");
                    }
                    return;
                }
                catch { }
            }
            match = Regex.Match(value, @"^([+-])?(0x|0b)?([0-9A-Fa-f]+)(u)?s$");
            if (match.Success)
            {
                int offsetBase = 10;
                if (match.Groups[2].Success)
                {
                    if (match.Groups[2].Value == "0b")
                        offsetBase = 2;
                    else if (match.Groups[2].Value == "0x")
                        offsetBase = 16;
                }
                try
                {
                    if (match.Groups[4].Success)
                    {
                        ushort shortValue = Convert.ToUInt16(match.Groups[3].Value, offsetBase);
                        mem.SafeWrite(address, BitConverter.GetBytes(shortValue));
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote ushort {shortValue} as value of {name} at 0x{address:X}");
                    }
                    else
                    {
                        short shortValue = Convert.ToInt16(match.Groups[3].Value, offsetBase);
                        if (match.Groups[1].Value == "-")
                            shortValue *= -1;
                        mem.SafeWrite(address, BitConverter.GetBytes(shortValue));
                        mLogger.WriteLine($"[Inaba Exe Patcher] Wrote short {shortValue} as value of {name} at 0x{address:X}");
                    }
                    return;
                }
                catch { }
            }
            match = Regex.Match(value, @"^([+-])?([0-9]+(?:\.[0-9]+)?)f$");
            if (match.Success && float.TryParse(match.Groups[2].Value, NumberStyles.Number, culture, out float floatValue))
            {
                if (match.Groups[1].Success)
                    floatValue *= -1;
                mem.SafeWrite(address, BitConverter.GetBytes(floatValue));
                mLogger.WriteLine($"[Inaba Exe Patcher] Wrote float {floatValue} as value of {name} at 0x{address:X}");
                return;
            }
            if (double.TryParse(value.Replace("d", ""), NumberStyles.Number, culture, out double doubleValue))
            {
                mem.SafeWrite(address, BitConverter.GetBytes(doubleValue));
                mLogger.WriteLine($"[Inaba Exe Patcher] Wrote double {doubleValue} as value of {name} at 0x{address:X}");
                return;
            }
            match = Regex.Match(value, @"bytes\((.*)\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var byteMatch = Regex.Matches(match.Groups[1].Value, @"([0-9A-Fa-f]{2})");
                if (byteMatch.Count == 0)
                {
                    mLogger.WriteLine($"[Inaba Exe Patcher] Found bytes identifier but no bytes. Bytes should be defined in hex such as bytes(EA 21 FB 8C E1)", Color.Red);
                    return;
                }
                List<byte> bytes = new List<byte>();
                foreach (Match m in byteMatch)
                {
                    bytes.Add(Convert.ToByte(m.Value, 16));
                }
                mem.SafeWrite(address, bytes.ToArray());
                mLogger.WriteLine($"[Inaba Exe Patcher] Wrote bytes {string.Join(" ", bytes.Select(b => b.ToString("X2")))} as value of {name} at 0x{address:X}");
                return;
            }

            var stringValueMatch = Regex.Match(value, "\"(.*)\"");
            if (!stringValueMatch.Success)
            {
                mLogger.WriteLine($"[Inaba Exe Patcher] Unable to parse {value} as an int, double, float or string not writing a value for {name}");
                return;
            }
            string stringValue = Regex.Unescape(stringValueMatch.Groups[1].Value);
            var stringBytes = encodingSetting.GetBytes(stringValue);
            if (stringBytes.Length < stringLength)
            {
                List<byte> byteList = stringBytes.ToList();
                while (byteList.Count < stringLength)
                    byteList.Add(0);
                stringBytes = byteList.ToArray();
            }
            mem.SafeWrite(address, stringBytes);
            mLogger.WriteLine($"[Inaba Exe Patcher] Wrote string \"{stringValue}\" as value of {name} at 0x{address:X}");
        }

        /// <summary>
        /// Searches for "//" in a string and removes it and anything after it
        /// </summary>
        /// <returns>A copy of the string with comments removed (or a copy of the string with no changes if there were no comments)</returns>
        private string RemoveComments(string text)
        {
            return Regex.Replace(text, @"\/\/.*", "");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            mProc?.Dispose();
        }
    }
}