﻿//  2006-2008 (c) Viva64.com Team
//  2008-2020 (c) OOO "Program Verification Systems"
//  2020-2022 (c) PVS-Studio LLC
using System;
using System.Data;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using ProgramVerificationSystems.PVSStudio.CommonTypes;
using ProgramVerificationSystems.PVSStudio;
using System.Xml;
using System.Xml.XPath;
using System.ComponentModel;
using System.Reflection;
using System.Diagnostics;
using Newtonsoft.Json;

namespace ProgramVerificationSystems.PlogConverter
{
    public enum TransformationMode 
    {
        toRelative,
        toAbsolute
    }

    /// <summary>
    ///     Output provider
    /// </summary>
    public sealed class PlogRenderFactory
    {
        public const string NoMessage = "No Messages Generated";

        private readonly List<ErrorInfoAdapter> _errors;
        private readonly ParsedArguments _parsedArgs;
        private readonly ApplicationSettings _settings;

        public ILogger Logger { get; private set; }

        private static readonly Comparison<ErrorInfoAdapter> DefaultSortStrategy = (first, second) =>
        {
            var firstErrorInfo = first.ErrorInfo;
            var secondErrorInfo = second.ErrorInfo;

            int comparsionResult = firstErrorInfo.AnalyzerType.CompareTo(secondErrorInfo.AnalyzerType);
            if (comparsionResult != 0)
                return comparsionResult;

            String projectNameSeparator = DataTableUtils.ProjectNameSeparator.ToString();
            comparsionResult = string.Compare(String.Join(projectNameSeparator, firstErrorInfo.ProjectNames),
                                              String.Join(projectNameSeparator, secondErrorInfo.ProjectNames),
                                              StringComparison.InvariantCulture);
            if (comparsionResult != 0)
                return comparsionResult;

            comparsionResult = string.Compare(firstErrorInfo.FileName, secondErrorInfo.FileName, EnvironmentUtils.OSDependentPathComparisonOption);
            if (comparsionResult != 0)
                return comparsionResult;

            comparsionResult = firstErrorInfo.Level.CompareTo(secondErrorInfo.Level);
            if (comparsionResult != 0)
                return comparsionResult;

            comparsionResult = string.Compare(firstErrorInfo.ErrorCode, secondErrorInfo.ErrorCode, StringComparison.InvariantCulture);
            if (comparsionResult != 0)
                return comparsionResult;

            comparsionResult = firstErrorInfo.LineNumber.CompareTo(secondErrorInfo.LineNumber);
            if (comparsionResult != 0)
                return comparsionResult;

            return string.Compare(firstErrorInfo.Message, secondErrorInfo.Message, StringComparison.InvariantCulture);
        };

        public PlogRenderFactory(ParsedArguments parsedArguments, ILogger logger = null)
        {
            _parsedArgs = parsedArguments;
            Logger = logger;
            if (!String.IsNullOrWhiteSpace(parsedArguments.SettingsPath))
                _settings = LoadSettings(parsedArguments.SettingsPath);

            var errorsSet = new HashSet<ErrorInfoAdapter>();
            
            foreach (var plog in _parsedArgs.RenderInfo.Logs)
            {
                string solutionPath;
                errorsSet.UnionWith(Utils.GetErrors(plog, out solutionPath));
            }

            foreach (var e in errorsSet)
                e.ErrorInfo.InternAllFields();

            _errors = new List<ErrorInfoAdapter>(errorsSet);
            IEnumerable<string> allDisabledErrors = null;

            if ((_parsedArgs.DisabledErrorCodes != null && _parsedArgs.DisabledErrorCodes.Count > 0) ||
                (_settings != null && !String.IsNullOrWhiteSpace(_settings.DisableDetectableErrors)))
                allDisabledErrors = Enumerable.Union(_parsedArgs.DisabledErrorCodes ?? new List<String>(),
                                                     _settings != null && !string.IsNullOrWhiteSpace(_settings.DisableDetectableErrors) ?
                                                         _settings.DisableDetectableErrors
                                                             .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList() :
                                                         new List<String>());

            _errors = Utils.FilterErrors(_errors, _parsedArgs.LevelMap, allDisabledErrors)
                           .FixTrialMessages()
                           .ToList();

            OrderErrors(_errors, _parsedArgs.RenderInfo.SrcRoot, _parsedArgs.RenderInfo.TransformationMode);
        }

        static public object[] GetPrimaryKey(ErrorInfo ei)
        {
            return new object[] { ei.LineNumber,
                                  ei.Message,
                                  ei.FileName,
                                  ei.ErrorCode };
        }

        public PlogRenderFactory(ParsedArguments parsedArguments, IEnumerable<ErrorInfoAdapter> errors, ILogger logger = null)
        {
            _parsedArgs = parsedArguments;
            _parsedArgs.RenderInfo.SrcRoot = _parsedArgs.RenderInfo.SrcRoot.Trim('"').TrimEnd('\\');
            Logger = logger;
            _errors = errors != null ? errors.ToList() : new List<ErrorInfoAdapter>();
            if (!String.IsNullOrWhiteSpace(parsedArguments.SettingsPath))
                _settings = LoadSettings(parsedArguments.SettingsPath);

            IEnumerable<string> allDisabledErrors = null;

            if ((_parsedArgs.DisabledErrorCodes != null && _parsedArgs.DisabledErrorCodes.Count > 0) ||
                (_settings != null && !String.IsNullOrWhiteSpace(_settings.DisableDetectableErrors)))
                allDisabledErrors = Enumerable.Union(_parsedArgs.DisabledErrorCodes ?? new List<String>(),
                                                     _settings != null && !string.IsNullOrWhiteSpace(_settings.DisableDetectableErrors) ?
                                                         _settings.DisableDetectableErrors
                                                             .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList() :
                                                         new List<String>());

            _errors = Utils.FilterErrors(_errors, _parsedArgs.LevelMap, allDisabledErrors)
                           .FixTrialMessages()
                           .ToList();

            OrderErrors(_errors, _parsedArgs.RenderInfo.SrcRoot, _parsedArgs.RenderInfo.TransformationMode);
        }

        private static void OrderErrors(List<ErrorInfoAdapter> errors, String sourceTreeRoot, TransformationMode transformationMode)
        {
            SortAnalyzedSourceFiles(errors, sourceTreeRoot, transformationMode);
            SortProjectNames(errors);
            errors.Sort(DefaultSortStrategy);
        }

        public static ApplicationSettings LoadSettings(String settingsPath)
        {
            ApplicationSettings settings = null;
            if (File.Exists(settingsPath))
              SettingsUtils.LoadSettings(settingsPath, ref settings);

            return settings;
        }

        private IPlogRenderer GetRenderService<T>(LogRenderType renderType, Action<LogRenderType, string> completedAction) where T : class, IPlogRenderer
        {
            var renderer = Activator.CreateInstance(typeof(T), new object[] { _parsedArgs.RenderInfo,
                                                                              _errors.ExcludeFalseAlarms(renderType),
                                                                              _parsedArgs.ErrorCodeMappings,
                                                                              _parsedArgs.OutputNameTemplate,
                                                                              renderType,
                                                                              Logger
                                                                            }) as T;
            if (completedAction != null)
                renderer.RenderComplete += (sender, args) => completedAction(renderType, args.OutputFile);

            return renderer;
        }

        public IPlogRenderer GetRenderService(LogRenderType renderType, Action<LogRenderType, string> completedAction = null)
        {
            switch (renderType)
            {
                case LogRenderType.Plog:
                    return GetRenderService<PlogToPlogRenderer>(renderType, completedAction);
                case LogRenderType.Totals:
                    return GetRenderService<PlogTotalsRenderer>(renderType, completedAction);
                case LogRenderType.Html:
                    return GetRenderService<HtmlPlogRenderer>(renderType, completedAction);
                case LogRenderType.Tasks:
                    return GetRenderService<TaskListRenderer>(renderType, completedAction);
                case LogRenderType.FullHtml:
                    return GetRenderService<FullHtmlRenderer>(renderType, completedAction);
                case LogRenderType.Txt:
                    return GetRenderService<PlogTxtRenderer>(renderType, completedAction);
                case LogRenderType.Csv:
                    return GetRenderService<CsvRenderer>(renderType, completedAction);
                case LogRenderType.TeamCity:
                    return GetRenderService<TeamCityPlogRenderer>(renderType, completedAction);
                case LogRenderType.Sarif:
                case LogRenderType.SarifVSCode:
                    return GetRenderService<SarifRenderer>(renderType, completedAction);
                case LogRenderType.JSON:
                    return GetRenderService<PlogJsonRenderer>(renderType, completedAction);
                case LogRenderType.MisraCompliance:
                    return GetRenderService<MisraComplianceRenderer>(renderType, completedAction);
                default:
                    goto case LogRenderType.Html;
            }
        }

        public static void SaveErrorsToJsonFile(IEnumerable<ErrorInfoAdapter> errors, string jsonLogPath)
        {
            JsonPvsReport jsonReport = new JsonPvsReport();
            jsonReport.AddRange(errors.Select(item => item.ErrorInfo));
            File.WriteAllText(jsonLogPath, JsonConvert.SerializeObject(jsonReport, Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
        }


        private static void DetectCodeMappings(IEnumerable<ErrorCodeMapping> errorCodeMappings, out bool hasCWE, out bool hasSAST)
        {
            hasCWE = false;
            hasSAST = false;

            foreach (var security in errorCodeMappings)
            {
                if (security == ErrorCodeMapping.CWE)
                    hasCWE = true;
                if (security == ErrorCodeMapping.MISRA || security == ErrorCodeMapping.OWASP || security == ErrorCodeMapping.AUTOSAR)
                    hasSAST = true;
            }
        }

        private static void SortAnalyzedSourceFiles(IEnumerable<ErrorInfoAdapter> errorInfoAdapters, String sourceTreeRoot, TransformationMode transformationMode)
        {
            foreach (var errorInfoAdapter in errorInfoAdapters)
            {
                errorInfoAdapter.ErrorInfo.AnalyzedSourceFiles
                                          .Select(e => PlogRenderUtils.ConvertPath(e, sourceTreeRoot, transformationMode))
                                          .OrderBy(e => e)
                                          .ToList();
            }
        }

        private static void SortProjectNames(IEnumerable<ErrorInfoAdapter> errorInfoAdapters)
        {
            foreach (var errorInfoAdapter in errorInfoAdapters)
                errorInfoAdapter.ErrorInfo.ProjectNames.Sort();
        }

        #region Implementation for CSV Output

        private class CsvRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";

            public string LogExtension { get; }
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            public CsvRenderer(RenderInfo renderInfo, 
                               IEnumerable<ErrorInfoAdapter> errors, 
                               IEnumerable<ErrorCodeMapping> errorCodeMappings,
                               string outputNameTemplate,
                               LogRenderType renderType,
                               ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var destFolder = RenderInfo.OutputDir;
                        var plogFilename = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                            ? OutputNameTemplate
                            : (RenderInfo.Logs.Count == 1 ? RenderInfo.Logs.First() : MergedReportName);
                        var csvPath = Path.Combine(destFolder, string.Format("{0}{1}", Path.GetFileName(plogFilename), LogExtension));
                        writer = new FileStream(csvPath, FileMode.Create);
                    }
                    using (var csvWriter = new CsvFileWriter(writer))
                    {
                        var headerRow = new CsvRow()
                        {
                            "FavIcon",
                            "Level",
                            "Error code"
                        };

                        DetectCodeMappings(ErrorCodeMappings, out bool hasCWE, out bool hasSAST);

                        if (hasCWE)
                            headerRow.Add("CWE");
                        if (hasSAST)
                            headerRow.Add("SAST");

                        headerRow.AddRange(new List<string>
                        {
                            "Message",
                            "Project",
                            "Short file",
                            "Line",
                            "False alarm",
                            "File",
                            "Analyzer",
                            "Analyzed source file(s)"
                        });

                        csvWriter.WriteRow(headerRow);

                        foreach (var error in Errors)
                        {
                            var messageRow = new CsvRow()
                            {
                                error.FavIcon.ToString(),
                                error.ErrorInfo.Level.ToString(),
                                error.ErrorInfo.ErrorCode
                            };

                            if (hasCWE)
                                messageRow.Add(error.ErrorInfo.ToCWEString());
                            if (hasSAST)
                                messageRow.Add(error.ErrorInfo.SastId);

                            messageRow.AddRange(new List<string>
                            {
                                error.ErrorInfo.Message,
                                String.Join("%", error.ErrorInfo.ProjectNames.ToArray()),
                                Path.GetFileName(error.ErrorInfo.FileName.Replace(ApplicationSettings.SourceTreeRootMarker, String.Empty)),
                                error.ErrorInfo.LineNumber.ToString(),
                                error.ErrorInfo.FalseAlarmMark.ToString(),
                                PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode),
                                error.ErrorInfo.AnalyzerType.ToString(),
                                string.Join("|", error.ErrorInfo.AnalyzedSourceFiles.Select(x => !string.IsNullOrWhiteSpace(x) ? Path.GetFileName(x.Replace(ApplicationSettings.SourceTreeRootMarker, RenderInfo.SrcRoot.Trim('"').TrimEnd('\\'))) 
                                                                                                                               : x))
                            });

                            csvWriter.WriteRow(messageRow);
                        }
                    }
                    if (writer is FileStream)
                        OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            private void OnRenderComplete(RenderCompleteEventArgs renderCompleteArgs)
            {
                RenderComplete?.Invoke(this, renderCompleteArgs);
            }
        }

        #endregion

        #region Implementation for TaskList Output

        private sealed class TaskListRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";
            private static readonly string StringFiller = new string('=', 15);

            public string LogExtension { get; }
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            public TaskListRenderer(RenderInfo renderInfo,
                                   IEnumerable<ErrorInfoAdapter> errors,
                                   IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                   string outputNameTemplate,
                                   LogRenderType renderType,
                                   ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var logName = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                        ? OutputNameTemplate
                        : (RenderInfo.Logs.Count == 1 ? Path.GetFileName(RenderInfo.Logs.First()) : MergedReportName);
                        var destDir = RenderInfo.OutputDir;
                        var tasksPath = Path.Combine(destDir, string.Format("{0}{1}", logName, LogExtension));
                        writer = new FileStream(tasksPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }
                    using (TextWriter tasksWriter = new StreamWriter(writer))
                    {
                        if (Errors != null && Errors.Any())
                            WriteTaskList(tasksWriter);
                        else
                            tasksWriter.WriteLine(String.Format("pvs-studio.com/en/docs/warnings\t1\terr\t{0}{1}", NoMessage, Environment.NewLine));
                        if (writer is FileStream)
                            OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            private void WriteTaskList(TextWriter tasksWriter)
            {
                foreach (var error in Errors)
                {
                    string level = String.Empty;
                    switch (error.ErrorInfo.Level)
                    {
                        case 1:
                            level = "err";
                            break;
                        case 2:
                            level = "warn";
                            break;
                        case 3:
                            level = "note";
                            break;
                        default:
                            level = "err";
                            break;
                    }

                    DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                    string securityCodes = string.Empty;

                    if (hasCWE && error.ErrorInfo.CweId != default(uint))
                        securityCodes += $"[{error.ErrorInfo.ToCWEString()}]";
                    
                    if (hasSAST && !string.IsNullOrEmpty(error.ErrorInfo.SastId))
                    {
                        if (!string.IsNullOrEmpty(securityCodes))
                            securityCodes += ' ';

                        securityCodes += $"[{error.ErrorInfo.SastId}]";
                    }

                    if (!string.IsNullOrEmpty(securityCodes))
                        securityCodes += ' ';

                    string lineMessage = String.Format("{0}\t{1}\t{2}\t{3} {4}{5}",
                                        PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode),
                                        error.ErrorInfo.LineNumber,
                                        level,
                                        error.ErrorInfo.ErrorCode,
                                        securityCodes + error.ErrorInfo.Message,
                                        Environment.NewLine);

                    tasksWriter.Write(lineMessage);
                }
            }

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }
        }

        #endregion

        #region Implementation for TeamCity Output

        private sealed class TeamCityPlogRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";
            public TeamCityPlogRenderer(RenderInfo renderInfo,
                                        IEnumerable<ErrorInfoAdapter> errors,
                                        IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                        string outputNameTemplate,
                                        LogRenderType renderType,
                                        ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var logName = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                            ? OutputNameTemplate
                            : (RenderInfo.Logs.Count == 1
                                ? Path.GetFileName(RenderInfo.Logs.First())
                                : MergedReportName);
                        var destDir = RenderInfo.OutputDir;
                        var tsPath = Path.Combine(destDir, string.Format("{0}{1}", logName, LogExtension));
                        writer = new FileStream(tsPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }

                    using (TextWriter tsWriter = new StreamWriter(writer))
                    {
                        if (Errors != null && Errors.Any())
                        {
                            WriteIssuesToStream(tsWriter);
                        }
                        else
                            tsWriter.WriteLine(NoMessage);

                        if (writer is FileStream)
                            OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }

            private void WriteIssuesToStream(TextWriter writer)
            {
                var inspectionsIDs = new HashSet<String>();

                foreach (var error in Errors)
                {
                    if (!inspectionsIDs.Contains(error.ErrorInfo.ErrorCode))
                    {
                        inspectionsIDs.Add(error.ErrorInfo.ErrorCode);

                        String errorURL = ErrorCodeUrlHelper.GetVivaUrlCode(error.ErrorInfo.ErrorCode, false);
                        writer.WriteLine($"##teamcity[inspectionType id='{error.ErrorInfo.ErrorCode}' name='{error.ErrorInfo.ErrorCode}' description='{errorURL}' category='{(ErrorCategory)error.ErrorInfo.Level}']");
                    }

                    String securityMessage = String.Empty;
                    DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                    var cwe = error.ErrorInfo.ToCWEString();
                    if (hasCWE && !String.IsNullOrEmpty(cwe))
                        securityMessage = cwe;

                    var sast = error.ErrorInfo.SastId;
                    if (hasSAST && !String.IsNullOrEmpty(sast))
                    {
                        if (String.IsNullOrEmpty(securityMessage))
                            securityMessage = sast;
                        else
                            securityMessage += $", {sast}";
                    }

                    String message = EscapeMessage(String.Format("{0}{1}",
                                                                 String.IsNullOrWhiteSpace(securityMessage) ? String.Empty
                                                                                                            : $"{securityMessage} ",
                                                                                                              error.ErrorInfo.Message));

                    String filePath = PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode);

                    writer.WriteLine($"##teamcity[inspection typeId='{error.ErrorInfo.ErrorCode}' message='{message}' file='{filePath}' line='{error.ErrorInfo.LineNumber}' SEVERITY='ERROR']");
                }

                String EscapeMessage(String messageToEscape)
                {
                    return  new StringBuilder(messageToEscape).Replace("|",  "||")
                                                              .Replace("'",  "|'")
                                                              .Replace("[",  "|[")
                                                              .Replace("]",  "|]")
                                                              .Replace("\r", "|r")
                                                              .Replace("\n", "|n")
                                                              .ToString();
                }
            }

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }
            enum ErrorCategory
            {
                Fails, High, Medium, Low
            }

            public string LogExtension { get; }
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }
            public event EventHandler<RenderCompleteEventArgs> RenderComplete;
        }
        #endregion

        #region Implementation for Html Output

        internal sealed class HtmlPlogRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";
            private const int MaxProjectNames = 5;
            private const int MaxAnalyzedSourceFiles = 5;
            private const int DefaultCWEColumnWidth = 6; //%
            private const int DefaultSASTColumnWidth = 9; //%
            private const int DefaultMessageColumntWidth = 45; //%

            private readonly string _htmlFoot;
            private readonly string _htmlHead;

            public HtmlPlogRenderer(RenderInfo renderInfo, 
                                    IEnumerable<ErrorInfoAdapter> errors,
                                    IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                    string outputNameTemplate,
                                    LogRenderType renderType,
                                    ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;

                #region HTML Templates
                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
                sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"en\">");
                sb.AppendLine("<head>");
                sb.AppendLine("<title>Messages</title>");
                sb.AppendLine("<meta http-equiv=\"content-type\" content=\"text/html; charset=utf-8\"/>");
                sb.AppendLine("<style type=\"text/css\">");
                sb.AppendLine("td");
                sb.AppendLine("{");
                sb.AppendLine("  padding: 0;");
                sb.AppendLine("  text-align: left;");
                sb.AppendLine("  vertical-align: top;");
                sb.AppendLine("}");
                sb.AppendLine("legend");
                sb.AppendLine("{");
                sb.AppendLine("  color: blue;");
                sb.AppendLine("  font: 1.2em bold Comic Sans MS Verdana;");
                sb.AppendLine("  text-align: center;");
                sb.AppendLine("}");
                sb.AppendLine("caption");
                sb.AppendLine("{");
                sb.AppendLine("  text-align:left;");
                sb.AppendLine("}");
                sb.AppendLine("</style>");
                sb.AppendLine("</head>");
                sb.AppendLine("<body>");
                sb.AppendLine("<table style=\"width: 100%; font: 12pt normal Century Gothic;\" >");
                sb.AppendLine("<caption style=\"font-weight: bold;background: #fff;color: #000;border: none !important;\">MESSAGES</caption>");
                sb.AppendLine("<tr style=\"background: black; color: white;\">");

                sb.AppendLine("<th style=\"width: 5%;\">Code</th>");
                DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                if (hasCWE)
                    sb.AppendLine($"<th style=\"width: {GetCWEColumntWidth()}%;\">CWE</th>");
                if (hasSAST)
                    sb.AppendLine($"<th style=\"width: {GetSASTColumntWidth()}%;\">SAST</th>");

                sb.AppendLine($"<th style=\"width: {GetMessageColumnWidth()}%;\">Message</th>");               
                sb.AppendLine("<th style=\"width: 10%;\">Project</th>");
                sb.AppendLine("<th style=\"width: 20%;\">File</th>");
                sb.AppendLine("<th style=\"width: 20%;\">Analyzed Source File(s)</th>");

                sb.AppendLine("</tr>");

                _htmlFoot = "</table></body></html>";
                _htmlHead = sb.ToString();

                sb.Clear();
                #endregion
            }

            public string LogExtension { get; }
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            private int GetMessageColumnWidth()
            {
                int width = DefaultMessageColumntWidth;
                DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                if (hasCWE)
                    width -= GetCWEColumntWidth();
                if (hasSAST)
                    width -= GetSASTColumntWidth();

                return width;
            }

            private int GetCWEColumntWidth()
            {
                return DefaultCWEColumnWidth;
            }

            private int GetSASTColumntWidth()
            {
                return DefaultSASTColumnWidth;
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var destFolder = RenderInfo.OutputDir;
                        var plogFilename = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                            ? OutputNameTemplate
                            : (RenderInfo.Logs.Count == 1 ? RenderInfo.Logs.First() : MergedReportName);
                        var htmlPath = Path.Combine(destFolder,
                            string.Format("{0}{1}", Path.GetFileName(plogFilename), LogExtension));
                        writer = new FileStream(htmlPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    }
                    using (var htmlWriter = new StreamWriter(writer))
                    {
                        if (Errors != null && Errors.Any())
                        {
                            htmlWriter.WriteLine(_htmlHead);
                            WriteHtmlTable(htmlWriter);
                            htmlWriter.WriteLine(_htmlFoot);
                        }
                        else
                        {
                            htmlWriter.WriteLine(
                                "<!DOCTYPE html>\n" +
                                    "<html>\n" +
                                    "<body>\n" +
                                    "<h3>{0}</h3>\n" +
                                    "</body>\n" +
                                    "</html>\n", NoMessage);
                        }
                    }
                    if (writer is FileStream)
                        OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                    writer.Close();
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            private void WriteHtmlTable(TextWriter writer)
            {
                var groupedErrorInfoMap = GroupByErrorInfo(Errors);
                var analyzerTypes = groupedErrorInfoMap.Keys;
                var colspan = 5;
                DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                if (hasCWE)
                    ++colspan;
                if (hasSAST)
                    ++colspan;

                foreach (var analyzerType in analyzerTypes)
                {
                    writer.WriteLine("<tr style='background: lightcyan;'>");
                    writer.WriteLine(
                        "<td colspan='{0}' style='color: red; text-align: center; font-size: 1.2em;'>{1}</td>",
                        colspan,
                        Utils.GetDescription(analyzerType));
                    writer.WriteLine("</tr>");
                    var groupedErrorInfo = groupedErrorInfoMap[analyzerType];
                    foreach (var error in groupedErrorInfo)
                    {
                        WriteTableRow(writer, error);
                    }
                }
            }

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }

            private void WriteTableRow(TextWriter writer, ErrorInfoAdapter error)
            {
                var errorCode = HttpUtility.HtmlEncode(error.ErrorInfo.ErrorCode);
                var message = HttpUtility.HtmlEncode(error.ErrorInfo.Message);
                string url = ErrorCodeUrlHelper.GetVivaUrlCode(error.ErrorInfo.ErrorCode, false);

                writer.WriteLine("<tr>");
                
                writer.WriteLine("<td style='width: 5%;'><a href='{0}'>{1}</a></td>", url, errorCode);

                DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                if (hasCWE)
                    writer.WriteLine("<td style='width: {0}%;'><a href='{1}'>{2}</a></td>",
                                     GetCWEColumntWidth(),
                                     ErrorCodeUrlHelper.GetCWEUrl(error.ErrorInfo),
                                     error.ErrorInfo.ToCWEString());
                if (hasSAST)
                    writer.WriteLine("<td style='width: {0}%;'>{1}</td>", GetSASTColumntWidth(), error.ErrorInfo.SastId);

                writer.WriteLine("<td style='width: {0}%;'>{1}</td>", GetMessageColumnWidth(), message);

                var projects = error.ErrorInfo.ProjectNames.ToArray();
                string projectsStr = (projects.Length > MaxProjectNames)
                    ? string.Join(", ", projects.Take(MaxProjectNames).ToArray().Concat(new string[] { "..." }))
                    : string.Join(", ", projects.Take(MaxProjectNames).ToArray());
                writer.WriteLine("<td style='width: 10%;'>{0}</td>", projectsStr);
                writer.Write("<td style='width: 20%;'>");

                string fileName = PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode);

                if (!String.IsNullOrWhiteSpace(fileName))
                    writer.WriteLine("<a href='file:///{0}'>{1} ({2})</a>",
                        fileName.Replace('\\', '/'), Path.GetFileName(PlogRenderUtils.RemoveSourceRootMarker(fileName)),
                        error.ErrorInfo.LineNumber.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("</td>");

                if (error.ErrorInfo.AnalyzedSourceFiles != null)
                {
                    var analyzedSourceFilesStr = string.Empty;
                    var count = Math.Min(error.ErrorInfo.AnalyzedSourceFiles.Count, MaxAnalyzedSourceFiles);
                    for (var i = 0; i < count; i++)
                    {
                        var analyzedSourceFile = error.ErrorInfo.AnalyzedSourceFiles[i];
                        if (!string.IsNullOrWhiteSpace(analyzedSourceFile))
                        {
                            analyzedSourceFile = PlogRenderUtils.ConvertPath(analyzedSourceFile, RenderInfo.SrcRoot, RenderInfo.TransformationMode);
                            if (i > 0 && i < count)
                                analyzedSourceFilesStr += ", ";
                            if (!string.IsNullOrWhiteSpace(analyzedSourceFile))
                                analyzedSourceFilesStr += string.Format("<a href='file:///{0}'>{1}</a>", analyzedSourceFile.Replace('\\', '/'), Path.GetFileName(analyzedSourceFile));
                        }
                    }

                    if (error.ErrorInfo.AnalyzedSourceFiles.Count > MaxAnalyzedSourceFiles)
                        analyzedSourceFilesStr += ", ...";

                    writer.WriteLine("<td style='width: 20%;'>{0}</td>", analyzedSourceFilesStr);
                }
                
                writer.WriteLine();

                writer.WriteLine("</tr>");
            }

            private static IDictionary<AnalyzerType, IList<ErrorInfoAdapter>> GroupByErrorInfo(
                IEnumerable<ErrorInfoAdapter> errors)
            {
                IDictionary<AnalyzerType, IList<ErrorInfoAdapter>> groupedErrorInfoMap =
                    new SortedDictionary<AnalyzerType, IList<ErrorInfoAdapter>>(Comparer<AnalyzerType>.Default);
                var types = Utils.GetEnumValues<AnalyzerType>();
                foreach (var analyzerType in types)
                {
                    groupedErrorInfoMap.Add(analyzerType, new List<ErrorInfoAdapter>());
                }

                foreach (var error in errors)
                {
                    groupedErrorInfoMap[error.ErrorInfo.AnalyzerType].Add(error);
                }

                foreach (var analyzerType in types.Where(analyzerType => groupedErrorInfoMap[analyzerType].Count == 0))
                {
                    groupedErrorInfoMap.Remove(analyzerType);
                }

                return groupedErrorInfoMap;
            }
        }

        #endregion

        #region Implementation for Summary Output

        private sealed class PlogTotalsRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";

            public string LogExtension { get; }
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            public PlogTotalsRenderer(RenderInfo renderInfo, 
                                      IEnumerable<ErrorInfoAdapter> errors,
                                      IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                      string outputNameTemplate,
                                      LogRenderType renderType,
                                      ILogger logger = null)
            {
                Logger = logger;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                RenderInfo = renderInfo;
                OutputNameTemplate = outputNameTemplate;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var plogFilename = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                        ? OutputNameTemplate
                        : (RenderInfo.Logs.Count == 1 ? Path.GetFileName(RenderInfo.Logs.First()) : MergedReportName);
                        var totalsPath = Path.Combine(RenderInfo.OutputDir, string.Format("{0}{1}", plogFilename, LogExtension));
                        writer = new FileStream(totalsPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    }
                    using (var summaryWriter = new StreamWriter(writer))
                    {
                        if (Errors != null && Errors.Any())
                            summaryWriter.WriteLine(CalculateSummary());
                        else
                            summaryWriter.WriteLine(NoMessage);
                        if (writer is FileStream)
                            OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }

            private string CalculateSummary()
            {
                int failIndex = 3;
                var totalStat = new Dictionary<AnalyzerType, int[]>
                {
                    {AnalyzerType.Unknown, new[] {0, 0, 0, 0}},
                    {AnalyzerType.General, new[] {0, 0, 0}},
                    {AnalyzerType.Optimization, new[] {0, 0, 0}},
                    //VivaMP is no longer supported by PVS-Studio, leaving for compatibility
                    {AnalyzerType.VivaMP, new[] {0, 0, 0}},
                    {AnalyzerType.Viva64, new[] {0, 0, 0}},
                    {AnalyzerType.CustomerSpecific, new[] {0, 0, 0}},
                    {AnalyzerType.MISRA, new[] {0, 0, 0}},
                    {AnalyzerType.AUTOSAR, new[] {0, 0, 0}},
                    {AnalyzerType.OWASP, new[] {0, 0, 0}},
                };

                foreach (
                    var error in
                        Errors.Where(
                            error =>
                                !error.ErrorInfo.FalseAlarmMark && error.ErrorInfo.Level >= 1 &&
                                error.ErrorInfo.Level <= 3))
                {
                    totalStat[error.ErrorInfo.AnalyzerType][error.ErrorInfo.Level - 1]++;
                }

                foreach (var error in Errors.Where(error => error.ErrorInfo.AnalyzerType == AnalyzerType.Unknown))
                    totalStat[AnalyzerType.Unknown][failIndex]++;

                var gaTotal = 0;
                for (var i = 0; i < totalStat[AnalyzerType.General].Length; i++)
                    gaTotal += totalStat[AnalyzerType.General][i];

                var opTotal = 0;
                for (var i = 0; i < totalStat[AnalyzerType.Optimization].Length; i++)
                    opTotal += totalStat[AnalyzerType.Optimization][i];

                var total64 = 0;
                for (var i = 0; i < totalStat[AnalyzerType.Viva64].Length; i++)
                    total64 += totalStat[AnalyzerType.Viva64][i];

                var csTotal = 0;
                for (var i = 0; i < totalStat[AnalyzerType.CustomerSpecific].Length; i++)
                    csTotal += totalStat[AnalyzerType.CustomerSpecific][i];

                var misraTotal = 0;
                for (var i = 0; i < totalStat[AnalyzerType.MISRA].Length; i++)
                    misraTotal += totalStat[AnalyzerType.MISRA][i];

                var autosarTotal = 0;
                for (var i = 0; i < totalStat[AnalyzerType.AUTOSAR].Length; i++)
                    autosarTotal += totalStat[AnalyzerType.AUTOSAR][i];

                var owaspTotal = 0;
                for (var i = 0; i < totalStat[AnalyzerType.OWASP].Length; i++)
                    owaspTotal += totalStat[AnalyzerType.OWASP][i];

                int l1Total = 0, l2Total = 0, l3Total = 0;
                //VivaMP is no longer supported by PVS-Studio, leaving for compatibility
                //Not counting Unknown errors (fails) in total statistics
                foreach (
                    var stat in
                        totalStat.Where(stat => stat.Key != AnalyzerType.Unknown && stat.Key != AnalyzerType.VivaMP))
                {
                    l1Total += stat.Value[0];
                    l2Total += stat.Value[1];
                    l3Total += stat.Value[2];
                }

                var total = l1Total + l2Total + l3Total;

                return $@"PVS - Studio analysis results
General Analysis L1:{totalStat[AnalyzerType.General][0]} + L2:{totalStat[AnalyzerType.General][1]} + L3:{totalStat[AnalyzerType.General][2]} = {gaTotal}
Optimization L1:{totalStat[AnalyzerType.Optimization][0]} + L2:{totalStat[AnalyzerType.Optimization][1]} + L3:{totalStat[AnalyzerType.Optimization][2]} = {opTotal}
64-bit issues L1:{totalStat[AnalyzerType.Viva64][0]} + L2:{totalStat[AnalyzerType.Viva64][1]} + L3:{totalStat[AnalyzerType.Viva64][2]} = {total64}
Customer Specific L1:{totalStat[AnalyzerType.CustomerSpecific][0]} + L2:{totalStat[AnalyzerType.CustomerSpecific][1]} + L3:{totalStat[AnalyzerType.CustomerSpecific][2]} = {csTotal}
MISRA L1:{totalStat[AnalyzerType.MISRA][0]} + L2:{totalStat[AnalyzerType.MISRA][1]} + L3:{totalStat[AnalyzerType.MISRA][2]} = {misraTotal}
AUTOSAR L1:{totalStat[AnalyzerType.AUTOSAR][0]} + L2:{totalStat[AnalyzerType.AUTOSAR][1]} + L3:{totalStat[AnalyzerType.AUTOSAR][2]} = {autosarTotal}
OWASP L1:{totalStat[AnalyzerType.OWASP][0]} + L2:{totalStat[AnalyzerType.OWASP][1]} + L3:{totalStat[AnalyzerType.OWASP][2]} = {owaspTotal}
Fails = {totalStat[AnalyzerType.Unknown][failIndex]}
Total L1:{l1Total} + L2:{l2Total} + L3:{l3Total} = {total}";
            }
        }

        #endregion

        #region Implementation for Text Output

        private sealed class PlogTxtRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";
            private static readonly string StringFiller = new string('=', 15);

            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            public string LogExtension { get; }

            public PlogTxtRenderer(RenderInfo renderInfo, 
                                   IEnumerable<ErrorInfoAdapter> errors,
                                   IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                   string outputNameTemplate,
                                   LogRenderType renderType,
                                   ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var logName = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                        ? OutputNameTemplate
                        : (RenderInfo.Logs.Count == 1 ? Path.GetFileName(RenderInfo.Logs.First()) : MergedReportName);
                        var destDir = RenderInfo.OutputDir;
                        var txtPath = Path.Combine(destDir, string.Format("{0}{1}", logName, LogExtension));
                        writer = new FileStream(txtPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }
                    using (TextWriter txtWriter = new StreamWriter(writer))
                    {
                        if (Errors != null && Errors.Any())
                            WriteText(txtWriter);
                        else
                            txtWriter.WriteLine(NoMessage);
                        if (writer is FileStream)
                            OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            private void WriteText(TextWriter txtWriter)
            {
                var outputIndex = 0;
                var currentType = AnalyzerType.Transient;
                foreach (var error in Errors)
                {
                    if (error.ErrorInfo.AnalyzerType != currentType)
                    {
                        currentType = error.ErrorInfo.AnalyzerType;
                        if (outputIndex != 0)
                        {
                            txtWriter.WriteLine();
                        }

                        txtWriter.WriteLine("{0}{1}{0}", StringFiller, Utils.GetDescription(currentType));
                    }

                    var message = GetOutput(error);
                    if (!message.EndsWith(Environment.NewLine))
                    {
                        message += Environment.NewLine;
                    }

                    txtWriter.Write(message);
                    outputIndex++;
                }
            }

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }

            private string GetOutput(ErrorInfoAdapter error)
            {
                DetectCodeMappings(ErrorCodeMappings, out var hasCWE, out var hasSAST);
                string securityCodes = string.Empty;          
                if (hasCWE && error.ErrorInfo.CweId != default(uint))
                    securityCodes += $"[{error.ErrorInfo.ToCWEString()}]";

                if (hasSAST && !string.IsNullOrEmpty(error.ErrorInfo.SastId))
                {
                    if (!string.IsNullOrEmpty(securityCodes))
                        securityCodes += ' ';

                    securityCodes += $"[{error.ErrorInfo.SastId}]";
                }

                if (!string.IsNullOrEmpty(securityCodes))
                    securityCodes += ' ';

                return error.ErrorInfo.Level >= 1 && error.ErrorInfo.Level <= 3
                    ? string.Format("{0} ({1}): error {2}: {3}{4}",
                        PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode),
                        error.ErrorInfo.LineNumber,
                        error.ErrorInfo.ErrorCode,
                        securityCodes + error.ErrorInfo.Message,
                        Environment.NewLine)
                    : GetFailOutput(error.ErrorInfo);
            }

            private string GetFailOutput(ErrorInfo errorInfo)
            {
                bool pathEmpty = String.IsNullOrWhiteSpace(errorInfo.FileName);
                bool codeEmpty = String.IsNullOrWhiteSpace(errorInfo.ErrorCode);
                var path = PlogRenderUtils.ConvertPath(errorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode);

                if (pathEmpty && !codeEmpty)
                    return $"error {errorInfo.ErrorCode}: {errorInfo.Message} {Environment.NewLine}";
                
                if (!pathEmpty && codeEmpty)
                    return $"{path} ({errorInfo.LineNumber}): {errorInfo.Message} {Environment.NewLine}";
                
                if (!pathEmpty && !codeEmpty)
                    return $"{path} ({errorInfo.LineNumber}): error {errorInfo.ErrorCode}: {errorInfo.Message} {Environment.NewLine}";

                return errorInfo.Message;
            }
        }

        #endregion

        #region BaseRender

        private abstract class BaseHtmlGeneratorRender : IPlogRenderer
        {
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            protected string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            public string LogExtension { get; }

            public BaseHtmlGeneratorRender(RenderInfo renderInfo,
                                   IEnumerable<ErrorInfoAdapter> errors,
                                   IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                   string outputNameTemplate,
                                   LogRenderType renderType,
                                   ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            private static string SaveErrorsToTempJsonFile(IEnumerable<ErrorInfoAdapter> errors)
            {
                string jsonLog = Path.GetTempFileName() + ".json";
                SaveErrorsToJsonFile(errors, jsonLog);
                return jsonLog;
            }

            private static string GetHtmlGenPath()
            {
                const string htmlGenExeName = "HtmlGenerator.exe";
                string htmlGenPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), htmlGenExeName);
                if (!File.Exists(htmlGenPath))
                    htmlGenPath = Path.Combine(EnvironmentUtils.GetModuleDirectory(), htmlGenExeName);
                return htmlGenPath;
            }

            private void Log(string data)
            {
                if (Logger != null)
                {
                    Logger.ErrorCode = 1;
                    Logger.LogError(data);
                }
                else
                    Console.Error.WriteLine(data);
            }

            private void ExecuteHtmlGenerator(string arguments)
            {
                using (Process htmlGenerator = new Process())
                {
                    var output = string.Empty;
                    var locker = new object();
                    DataReceivedEventHandler handler = delegate (object sender, DataReceivedEventArgs e)
                    {
                        lock (locker)
                            output += e.Data + Environment.NewLine;
                    };

                    htmlGenerator.StartInfo.FileName = GetHtmlGenPath();
                    htmlGenerator.StartInfo.Arguments = arguments;
                    htmlGenerator.StartInfo.UseShellExecute = false;
                    htmlGenerator.StartInfo.CreateNoWindow = true;
                    htmlGenerator.StartInfo.RedirectStandardError = true;
                    htmlGenerator.ErrorDataReceived += handler;
                    htmlGenerator.Start();
                    htmlGenerator.BeginErrorReadLine();
                    htmlGenerator.WaitForExit();

                    if (htmlGenerator.ExitCode != 0 && !string.IsNullOrEmpty(output.Trim()))
                    {
                        Log(output);
                    }
                }
            }

            abstract protected (string, string) GetGenerateData(string jsonLog);
            public void Render(Stream writer = null)
            {
                string jsonLog = "";
                try
                {
                    jsonLog = SaveErrorsToTempJsonFile(Errors);
                    var (arguments, outputFile) = GetGenerateData(jsonLog);
                    ExecuteHtmlGenerator(arguments);
                    OnRenderComplete(new RenderCompleteEventArgs(outputFile));
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log(ex.Message);
                }
                catch (IOException ex)
                {
                    Log(ex.Message);
                }
                finally
                {
                    File.Delete(jsonLog);
                }
            }


            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }
        }

        #endregion

        #region Implementation for FullHtml Output

        private sealed class FullHtmlRenderer : BaseHtmlGeneratorRender
        {
            public FullHtmlRenderer(RenderInfo renderInfo, IEnumerable<ErrorInfoAdapter> errors, IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                   string outputNameTemplate, LogRenderType renderType,ILogger logger = null) 
                : base(renderInfo, errors, errorCodeMappings, outputNameTemplate, renderType, logger)
            {
            }

            protected override (string, string) GetGenerateData(string jsonLog)
            {
                string defaultFullHtmlDir = Path.Combine(RenderInfo.OutputDir, "fullhtml");
                if (!string.IsNullOrEmpty(OutputNameTemplate))
                    defaultFullHtmlDir = Path.Combine(RenderInfo.OutputDir, OutputNameTemplate);

                if (Directory.Exists(defaultFullHtmlDir))
                    Directory.Delete(defaultFullHtmlDir, true);

                string arguments = $" \"{jsonLog}\" -t fullhtml -o \"{Path.Combine(RenderInfo.OutputDir, OutputNameTemplate).TrimEnd(new char[] { '\\', '/' })}\" -r \"{RenderInfo.SrcRoot.TrimEnd(new char[] { '\\', '/' })}\" -a \"GA;64;OP;CS;MISRA;AUTOSAR;OWASP\"";
                foreach (var security in ErrorCodeMappings)
                    arguments += " -m " + security.ToString().ToLower();

                return (arguments, defaultFullHtmlDir);
            }
        }

         #endregion

        #region Implementation for Plog-to-Plog Output

        private sealed class PlogToPlogRenderer : IPlogRenderer
        {
            private IList<String> _solutionPaths, _solutionVersions, _plogVersions;
            private const string MergedReportName = "MergedReport",
                TrialRestriction = "TRIAL RESTRICTION",
                NoVersionSolution = "Independent",
                NO_VERSION_PLOG = "1";

            public string LogExtension { get; }
            private string OutputNameTemplate { get; set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            public RenderInfo RenderInfo { get; private set; }

            public ILogger Logger { get; private set; }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;

            public PlogToPlogRenderer(RenderInfo renderInfo, 
                                      IEnumerable<ErrorInfoAdapter> errors,
                                      IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                      string outputNameTemplate,
                                      LogRenderType renderType,
                                      ILogger logger)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;

                foreach (var error in Errors)
                {
                    error.ErrorInfo.FileName = PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode, true);
                    error.ErrorInfo.Positions = PlogRenderUtils.ConvertPositions(error.ErrorInfo.Positions, RenderInfo.SrcRoot, RenderInfo.TransformationMode);
                    error.ErrorInfo.AnalyzedSourceFiles = PlogRenderUtils.ConvertPaths(error.ErrorInfo.AnalyzedSourceFiles, RenderInfo.SrcRoot, RenderInfo.TransformationMode);
                }

                var plogXmlDocument = new XmlDocument();
                _solutionPaths = new List<String>();
                _solutionVersions = new List<String>();
                _plogVersions = new List<String>();

                var xmlLogs = renderInfo.Logs.Where(logPath => Path.GetExtension(logPath).Equals(Utils.PlogExtension, StringComparison.OrdinalIgnoreCase));
                if (!xmlLogs.Any())
                {
                    _solutionPaths.Add(string.Empty);
                    _plogVersions.Add(NO_VERSION_PLOG);
                }
                else
                {
                    foreach (var logPath in xmlLogs)
                    {
                        try
                        {
                            plogXmlDocument.LoadXml(File.ReadAllText(logPath, Encoding.UTF8));
                            var solutionNodeList = plogXmlDocument.SelectNodes("//NewDataSet/Solution_Path");
                            string solutionItem = solutionNodeList[0]["SolutionPath"].InnerText ?? string.Empty;
                            _solutionPaths.Add(PlogRenderUtils.ConvertPath(solutionItem, RenderInfo.SrcRoot, RenderInfo.TransformationMode, true));

                            if (!string.IsNullOrWhiteSpace(solutionNodeList[0]["SolutionVersion"].InnerText))
                                _solutionVersions.Add(solutionNodeList[0]["SolutionVersion"].InnerText);

                            _plogVersions.Add(solutionNodeList[0]["PlogVersion"].InnerText ?? NO_VERSION_PLOG);
                        }
                        catch (XmlException)
                        {
                            _solutionPaths.Add(string.Empty);
                            _plogVersions.Add(NO_VERSION_PLOG);
                        }
                        catch (XPathException e)
                        {
                            throw new XmlException(e.Message, e);
                        }
                    }
                }

                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
            }

            private void FillMetadataTable(DataTable solutionPathsTable)
            {
                solutionPathsTable.Rows.Clear();
                var rw = solutionPathsTable.NewRow();
                if (_solutionPaths.Count() > 1)
                {
                    var firstSolution = _solutionPaths.First();
                    if (_solutionPaths.All((solutionPath) => String.Equals(solutionPath, firstSolution, StringComparison.InvariantCultureIgnoreCase)))
                        rw[DataColumnNames.SolutionPath] = firstSolution;
                    else
                        rw[DataColumnNames.SolutionPath] = string.Empty;
                }
                else if (_solutionPaths.Count() == 1)
                    rw[DataColumnNames.SolutionPath] = _solutionPaths.First();
                else
                    rw[DataColumnNames.SolutionPath] = string.Empty;

                try
                {
                    rw[DataColumnNames.SolutionVer] = !_solutionVersions.Any() ?
                                                      NoVersionSolution :
                                                      _solutionVersions.Select((solutionVersion) => double.Parse(solutionVersion, CultureInfo.InvariantCulture))
                                                                       .Max()
                                                                       .ToString("N1",CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    rw[DataColumnNames.SolutionVer] = NoVersionSolution;
                }

                rw[DataColumnNames.PlogVersion] = DataTableUtils.PlogVersion;
                rw[DataColumnNames.PlogModificationDate] = DateTime.UtcNow;
                solutionPathsTable.Rows.Add(rw);
            }

            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }

            public void Render(Stream writer = null)
            {
                try
                {
                    if (writer == null)
                    {
                        var plogFilename = !string.IsNullOrWhiteSpace(OutputNameTemplate)
                            ? OutputNameTemplate
                            : (RenderInfo.Logs.Count == 1 ? Path.GetFileNameWithoutExtension(RenderInfo.Logs.First()) + "_filtered" : MergedReportName);
                        var totalPath = Path.Combine(RenderInfo.OutputDir, string.Format("{0}{1}", plogFilename , LogExtension));
                        writer = new FileStream(totalPath, FileMode.Create);
                    }
                    var solutionPathsTable = new DataTable();
                    var allMessages = new DataTable();

                    DataTableUtils.CreatePVSDataTable(allMessages, solutionPathsTable, allMessages.DefaultView);
                    var errorsInfo = Errors.ToList().ConvertAll((error) => error.ErrorInfo);
                    foreach (var errorInfo in errorsInfo)
                        DataTableUtils.AppendErrorInfoToDataTable(allMessages, errorInfo);

                    FillMetadataTable(solutionPathsTable);

                    var dataset = new DataSet();
                    dataset.Tables.Add(solutionPathsTable);
                    dataset.Tables.Add(allMessages);

                    dataset.WriteXml(writer, XmlWriteMode.WriteSchema);

                    if (writer is FileStream)
                        OnRenderComplete(new RenderCompleteEventArgs((writer as FileStream).Name));
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorCode = 1;
                        Logger.LogError(ex.Message);
                    }
                    else
                        Console.Error.WriteLine(ex.Message);
                }
            }
        }

        #endregion

        #region Implementation for SARIF Output

        private sealed class SarifRenderer : BaseHtmlGeneratorRender
        {
            private bool IsForVSCode { get; set; }

            public SarifRenderer(RenderInfo renderInfo, IEnumerable<ErrorInfoAdapter> errors, IEnumerable<ErrorCodeMapping> errorCodeMappings,
                                   string outputNameTemplate, LogRenderType renderType, ILogger logger = null) 
                : base(renderInfo, errors, errorCodeMappings, outputNameTemplate, renderType, logger)
            {
                IsForVSCode = renderType == LogRenderType.SarifVSCode;
            }

            protected override (string, string) GetGenerateData(string jsonLog)
            {
                var logName = !string.IsNullOrWhiteSpace(OutputNameTemplate) ? OutputNameTemplate : (RenderInfo.Logs.Count == 1 ? Path.GetFileName(RenderInfo.Logs.First()) : "MergedReport");
                var fileName = Path.Combine(RenderInfo.OutputDir, $"{logName}{LogExtension}").TrimEnd(new char[] { '\\', '/' });
                var sourceRoot = RenderInfo.SrcRoot.TrimEnd(new char[] { '\\', '/' });
                var outputMode = IsForVSCode ? "sarif-vscode" : "sarif";
                string arguments = $" \"{jsonLog}\" -t {outputMode} -o \"{fileName}\" -r \"{sourceRoot}\" -a \"GA;64;OP;CS;MISRA;AUTOSAR;OWASP\"";
                foreach (var security in ErrorCodeMappings)
                    arguments += " -m " + security.ToString().ToLower();

                return (arguments, fileName);
            }
        }

        #endregion

        #region Implementation for JSON output

        private sealed class PlogJsonRenderer : IPlogRenderer
        {
            private const string MergedReportName = "MergedReport";
            public RenderInfo RenderInfo { get; private set; }
            public IEnumerable<ErrorInfoAdapter> Errors { get; private set; }
            public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; private set; }
            private string OutputNameTemplate { get; set; }
            public ILogger Logger { get; private set; }

            public string LogExtension { get; }
            public PlogJsonRenderer(RenderInfo renderInfo,
                       IEnumerable<ErrorInfoAdapter> errors,
                       IEnumerable<ErrorCodeMapping> errorCodeMappings,
                       string outputNameTemplate,
                       LogRenderType renderType,
                       ILogger logger = null)
            {
                RenderInfo = renderInfo;
                Errors = errors;
                ErrorCodeMappings = errorCodeMappings;
                OutputNameTemplate = outputNameTemplate;
                Logger = logger;
                LogExtension = ReflectionUtils.GetAttributes<DescriptionAttribute, LogRenderType>(renderType.ToString(),
                                                                                                  BindingFlags.Static | BindingFlags.Public)
                                              .First().Description;
                foreach (var error in Errors)
                {
                    error.ErrorInfo.FileName = PlogRenderUtils.ConvertPath(error.ErrorInfo.FileName, RenderInfo.SrcRoot, RenderInfo.TransformationMode, true);
                    error.ErrorInfo.Positions = PlogRenderUtils.ConvertPositions(error.ErrorInfo.Positions, RenderInfo.SrcRoot, RenderInfo.TransformationMode);
                }
            }

            public void Render(Stream writer = null)
            {
                var destDir = RenderInfo.OutputDir;
                var jsonLogName = !string.IsNullOrWhiteSpace(OutputNameTemplate) ? OutputNameTemplate
                                                                                 : (RenderInfo.Logs.Count == 1 ? RenderInfo.Logs.First() 
                                                                                                               : MergedReportName);

                var jsonLogPath = Path.Combine(destDir, string.Format("{0}{1}", Path.GetFileName(jsonLogName), LogExtension));

                SaveErrorsToJsonFile(Errors, jsonLogPath);
                OnRenderComplete(new RenderCompleteEventArgs(jsonLogPath));
            }

            public event EventHandler<RenderCompleteEventArgs> RenderComplete;
            private void OnRenderComplete(RenderCompleteEventArgs renderComplete)
            {
                RenderComplete?.Invoke(this, renderComplete);
            }

        }

        #endregion

        #region Implementation for Compliance output
        private sealed class MisraComplianceRenderer : BaseHtmlGeneratorRender
        {
            public MisraComplianceRenderer(RenderInfo renderInfo, IEnumerable<ErrorInfoAdapter> errors, IEnumerable<ErrorCodeMapping> errorCodeMappings, 
                                      string outputNameTemplate, LogRenderType renderType, ILogger logger = null)
                : base(renderInfo, errors, errorCodeMappings, outputNameTemplate, renderType, logger)
            {
            }

            protected override (string, string) GetGenerateData(string jsonLog)
            {
                string defaultComplianceDir = Path.Combine(RenderInfo.OutputDir, "misracompliance");
                if (!string.IsNullOrEmpty(OutputNameTemplate))
                {
                    if (RenderInfo.AllLogRenderType)
                    {
                        defaultComplianceDir = Path.Combine(RenderInfo.OutputDir, $"{OutputNameTemplate}_misra");
                    }
                    else
                    {
                        defaultComplianceDir = Path.Combine(RenderInfo.OutputDir, OutputNameTemplate);
                    }
                }
                    
                if (Directory.Exists(defaultComplianceDir))
                    Directory.Delete(defaultComplianceDir, true);

                string arguments = $" \"{jsonLog}\" -t misra_compliance -o \"{defaultComplianceDir.TrimEnd(new char[] { '\\', '/' })}\"";

                if (!String.IsNullOrEmpty(RenderInfo.GRP))
                    arguments += $" --grp \"{RenderInfo.GRP}\"";

                if (!String.IsNullOrEmpty(RenderInfo.MisraDeviations))
                    arguments += $" --misra_deviations \"{RenderInfo.MisraDeviations}\"";
 
                return (arguments, defaultComplianceDir);
            }
        }

        #endregion
    }

    public class ParsedArguments
    {
        public RenderInfo RenderInfo { get; set; }
        public IDictionary<AnalyzerType, ISet<uint>> LevelMap { get; set; }
        public ISet<LogRenderType> RenderTypes { get; set; }
        public IEnumerable<ErrorCodeMapping> ErrorCodeMappings { get; set; }
        public IList<string> DisabledErrorCodes { get; set; }
        public String SettingsPath { get; set; }
        public String OutputNameTemplate { get; set; }
        public Boolean IndicateWarnings { get; set; }
    }

    public class RenderInfo
    {
        private IList<string> _plogs = new List<string>();
        public IList<string> Logs { get { return _plogs; } }
        public string OutputDir { get; set; }
        public string SrcRoot { get; set; }
        public TransformationMode TransformationMode { get; set; }
        public string GRP { get; set; } = String.Empty;
        public string MisraDeviations { get; set; } = String.Empty;
        public bool AllLogRenderType { get; set; } = false;
    }

    class PlogRenderUtils
    {
        public static string ConvertPath(string file, string srcRootDir, TransformationMode transformMode, bool addMarker = false)
        {
            if (string.IsNullOrWhiteSpace(srcRootDir))
                return addMarker ? file : RemoveSourceRootMarker(file);
            
            var srcRoot = srcRootDir.Trim('"');
            if (transformMode == TransformationMode.toAbsolute)
            {
                return PathUtils.TransformPathToAbsolute(file, srcRootDir);
            }
            else
            {
                string relativePath = RemoveSourceRootMarker(PathUtils.TransformPathToRelative(file, srcRootDir));
                return !relativePath.StartsWith(ApplicationSettings.SourceTreeRootMarker) && addMarker ? 
                    ApplicationSettings.SourceTreeRootMarker + Path.DirectorySeparatorChar + relativePath.Trim(Path.DirectorySeparatorChar) : relativePath;
            }
        }

        public static string RemoveSourceRootMarker(string path)
        {
            return path.Replace(ApplicationSettings.SourceTreeRootMarker, string.Empty);
        }

        public static SourceFilePositions ConvertPositions(SourceFilePositions positions, string srcRootDir, TransformationMode transformMode)
        {
            if (string.IsNullOrWhiteSpace(srcRootDir))
            {
                return positions;
            }
            SourceFilePositions convertedPositions = new SourceFilePositions();
            foreach (SourceFilePosition position in positions)
            {
                string convertedPath = PlogRenderUtils.ConvertPath(position.FilePath, srcRootDir, transformMode, true);
                convertedPositions.Add(new SourceFilePosition(convertedPath, position.LineNumber));
            }
            return convertedPositions;
        }

        public static List<string> ConvertPaths(List<string> paths, string srcRootDir, TransformationMode transformMode)
        {
            if (string.IsNullOrWhiteSpace(srcRootDir))
                return paths;

            var convertedPositions = new List<string>();
            foreach (var path in paths)
                convertedPositions.Add(PlogRenderUtils.ConvertPath(path, srcRootDir, transformMode, true));

            return convertedPositions;
        }

    }
}