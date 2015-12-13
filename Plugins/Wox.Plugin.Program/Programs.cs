﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using IWshRuntimeLibrary;
using Wox.Infrastructure;
using Wox.Plugin.Program.ProgramSources;
using Wox.Infrastructure.Logger;
using Stopwatch = Wox.Infrastructure.Stopwatch;

namespace Wox.Plugin.Program
{
    public class Programs : ISettingProvider, IPlugin, IPluginI18n, IContextMenu
    {
        private static object lockObject = new object();
        private static List<Program> programs = new List<Program>();
        private static List<IProgramSource> sources = new List<IProgramSource>();
        private static Dictionary<string, Type> SourceTypes = new Dictionary<string, Type>() {
            {"FileSystemProgramSource", typeof(FileSystemProgramSource)},
            {"CommonStartMenuProgramSource", typeof(CommonStartMenuProgramSource)},
            {"UserStartMenuProgramSource", typeof(UserStartMenuProgramSource)},
            {"AppPathsProgramSource", typeof(AppPathsProgramSource)},
        };
        private PluginInitContext context;

        public List<Result> Query(Query query)
        {

            var fuzzyMather = FuzzyMatcher.Create(query.Search);
            var results = programs.Where(o => MatchProgram(o, fuzzyMather)).
                                   Select(ScoreFilter).
                                   OrderByDescending(o => o.Score)
                                   .Select(c => new Result()
                                   {
                                       Title = c.Title,
                                       SubTitle = c.ExecutePath,
                                       IcoPath = c.IcoPath,
                                       Score = c.Score,
                                       ContextData = c,
                                       Action = (e) =>
                                       {
                                           context.API.HideApp();
                                           context.API.ShellRun(c.ExecutePath);
                                           return true;
                                       }
                                   }).ToList();
            return results;
        }

        static string ResolveShortcut(string filePath)
        {
            // IWshRuntimeLibrary is in the COM library "Windows Script Host Object Model"
            WshShell shell = new WshShell();
            IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(filePath);
            return shortcut.TargetPath;
        }

        private bool MatchProgram(Program program, FuzzyMatcher matcher)
        {
            var scores = new List<string> { program.Title, program.PinyinTitle, program.AbbrTitle, program.ExecuteName };
            program.Score = scores.Select(s => matcher.Evaluate(s ?? string.Empty).Score).Max();
            if (program.Score > 0) return true;
            else return false;
        }

        public void Init(PluginInitContext context)
        {
            this.context = context;
            this.context.API.ResultItemDropEvent += API_ResultItemDropEvent;
            Stopwatch.Debug("Preload programs", () =>
            {
                programs = ProgramCacheStorage.Instance.Programs;
            });
            Log.Info($"Preload {programs.Count} programs from cache");
            Stopwatch.Debug("Program Index", IndexPrograms);
        }

        void API_ResultItemDropEvent(Result result, IDataObject dropObject, DragEventArgs e)
        {

            e.Handled = true;
        }

        public static void IndexPrograms()
        {
            lock (lockObject)
            {
                List<ProgramSource> programSources = new List<ProgramSource>();
                programSources.AddRange(LoadDeaultProgramSources());
                if (ProgramStorage.Instance.ProgramSources != null &&
                    ProgramStorage.Instance.ProgramSources.Count(o => o.Enabled) > 0)
                {
                    programSources.AddRange(ProgramStorage.Instance.ProgramSources);
                }

                sources.Clear();
                foreach (var source in programSources.Where(o => o.Enabled))
                {
                    Type sourceClass;
                    if (SourceTypes.TryGetValue(source.Type, out sourceClass))
                    {
                        ConstructorInfo constructorInfo = sourceClass.GetConstructor(new[] { typeof(ProgramSource) });
                        if (constructorInfo != null)
                        {
                            IProgramSource programSource =
                                constructorInfo.Invoke(new object[] { source }) as IProgramSource;
                            sources.Add(programSource);
                        }
                    }
                }

                var tempPrograms = new List<Program>();
                foreach (var source in sources)
                {
                    var list = source.LoadPrograms();
                    list.ForEach(o =>
                    {
                        o.Source = source;
                    });
                    tempPrograms.AddRange(list);
                }

                // filter duplicate program
                programs = tempPrograms.GroupBy(x => new { x.ExecutePath, x.ExecuteName })
                    .Select(g => g.First()).ToList();

                ProgramCacheStorage.Instance.Programs = programs;
                ProgramCacheStorage.Instance.Save();
            }
        }

        /// <summary>
        /// Load program sources that wox always provide
        /// </summary>
        private static List<ProgramSource> LoadDeaultProgramSources()
        {
            var list = new List<ProgramSource>();
            list.Add(new ProgramSource()
            {
                BonusPoints = 0,
                Enabled = ProgramStorage.Instance.EnableStartMenuSource,
                Type = "CommonStartMenuProgramSource"
            });
            list.Add(new ProgramSource()
            {
                BonusPoints = 0,
                Enabled = ProgramStorage.Instance.EnableStartMenuSource,
                Type = "UserStartMenuProgramSource"
            });
            list.Add(new ProgramSource()
            {
                BonusPoints = -10,
                Enabled = ProgramStorage.Instance.EnableRegistrySource,
                Type = "AppPathsProgramSource"
            });
            return list;
        }

        private Program ScoreFilter(Program p)
        {
            p.Score += p.Source.BonusPoints;

            if (p.Title.Contains("启动") || p.Title.ToLower().Contains("start"))
                p.Score += 10;

            if (p.Title.Contains("帮助") || p.Title.ToLower().Contains("help") || p.Title.Contains("文档") || p.Title.ToLower().Contains("documentation"))
                p.Score -= 10;

            if (p.Title.Contains("卸载") || p.Title.ToLower().Contains("uninstall"))
                p.Score -= 20;
            return p;
        }

        #region ISettingProvider Members

        public System.Windows.Controls.Control CreateSettingPanel()
        {
            return new ProgramSetting(context);
        }

        #endregion

        public string GetLanguagesFolder()
        {
            return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Languages");
        }
        public string GetTranslatedPluginTitle()
        {
            return context.API.GetTranslation("wox_plugin_program_plugin_name");
        }

        public string GetTranslatedPluginDescription()
        {
            return context.API.GetTranslation("wox_plugin_program_plugin_description");
        }

        public List<Result> LoadContextMenus(Result selectedResult)
        {
            Program p = selectedResult.ContextData as Program;
            List<Result> contextMenus = new List<Result>()
            {
                new Result()
                {
                    Title = context.API.GetTranslation("wox_plugin_program_run_as_administrator"),
                    Action = _ =>
                    {
                        context.API.HideApp();
                        context.API.ShellRun(p.ExecutePath, true);
                        return true;
                    },
                    IcoPath = "Images/cmd.png"
                },
                new Result()
                {
                    Title = context.API.GetTranslation("wox_plugin_program_open_containing_folder"),
                    Action = _ =>
                    {
                        context.API.HideApp();
                        String Path = p.ExecutePath;
                        //check if shortcut
                        if (Path.EndsWith(".lnk"))
                        {
                            //get location of shortcut
                            var resolved = ResolveShortcut(Path);
                            if(!string.IsNullOrEmpty(resolved))
                                Path = resolved;
                        }
                        //get parent folder
                        Path = Directory.GetParent(Path).FullName;
                        //open the folder
                        context.API.ShellRun("explorer.exe " + Path, false);
                        return true;
                    },
                    IcoPath = "Images/folder.png"
                }
            };
            return contextMenus;
        }
    }
}