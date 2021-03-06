﻿#region License
//  Copyright 2015 John Källén
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//      http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pytocs.Syntax;
using Pytocs.Types;
using Name = Pytocs.Syntax.Identifier;
using System.Diagnostics;

namespace Pytocs.TypeInference
{
    public interface Analyzer
    {
        IFileSystem FileSystem { get; }
        DataTypeFactory TypeFactory { get; }
        int nCalled { get; set; }
        State globaltable { get; }
        HashSet<Name> Resolved { get; }
        HashSet<Name> unresolved { get; }

        DataType LoadFile(string path);
        DataType LoadModule(List<Name> name, State state);
        Module getAstForFile(string file);
        string GetModuleQname(string file);
        IEnumerable<Binding> GetModuleBindings();

        Binding CreateBinding(string id, Node node, DataType type, BindingKind kind);
        void addRef(AttributeAccess attr, DataType targetType, ISet<Binding> bs);
        void putRef(Node node, ICollection<Binding> bs);
        void putRef(Node node, Binding bs);
        void AddUncalled(FunType f);
        void RemoveUncalled(FunType f);
        void pushStack(Exp v);
        void popStack(Exp v);
        bool inStack(Exp v);

        string ModuleName(string path);
        string ExtendPath(string path, string name);

        void putProblem(Node loc, string msg);
        void putProblem(string filename, int start, int end, string msg);

        //void msg(string message);
        void msg_(string message);
        string Percent(long num, long total);
    }

    /// <summary>
    /// Analyzes a directory of Python files, collecting 
    /// and inferring type information as it parses all 
    /// the files.
    /// </summary>
    public class AnalyzerImpl : Analyzer
    {
        //public const string MODEL_LOCATION = "org/yinwang/pysonar/models";
        private List<string> loadedFiles = new List<string>();
        private List<Binding> allBindings = new List<Binding>();
        private Dictionary<Node, List<Binding>> references = new Dictionary<Node, List<Binding>>(); 
        private Dictionary<string, List<Diagnostic>> semanticErrors = new Dictionary<string, List<Diagnostic>>();
        private Dictionary<string, List<Diagnostic>> parseErrors = new Dictionary<string, List<Diagnostic>>();
        private string cwd = null;
        private List<string> path = new List<string>();
        private HashSet<FunType> uncalled = new HashSet<FunType>();
        private HashSet<object> callStack = new HashSet<object>();
        private HashSet<object> importStack = new HashSet<object>();

        private AstCache astCache;
        private string cacheDir;
        private HashSet<string> failedToParse = new HashSet<string>();
        private IProgress loadingProgress;
        private string projectDir;
        private readonly string suffix;
        private ILogger logger;

        public Dictionary<string, object> options;
        private DateTime startTime;

        public AnalyzerImpl(ILogger logger)
            : this(new FileSystem(), logger, new Dictionary<string, object>(), DateTime.Now)
        {
        }

        public AnalyzerImpl(IFileSystem fs, ILogger logger, Dictionary<string, object> options, DateTime startTime)
        {
            this.FileSystem = fs;
            this.logger = logger;
            this.TypeFactory = new DataTypeFactory(this);
            this.globaltable = new State(null, State.StateType.GLOBAL);
            this.Resolved = new HashSet<Name>();
            this.unresolved = new HashSet<Name>();

            if (options != null)
            {
                this.options = options;
            }
            else
            {
                this.options = new Dictionary<string, object>();
            }
            this.startTime = startTime;
            this.suffix = ".py";
            this.Builtins = new Builtins(this);
            this.Builtins.Initialize();
            AddPythonPath();
            CopyModels();
            CreateCacheDirectory();
            GetAstCache();
        }

        public IFileSystem FileSystem { get; private set; }
        public DataTypeFactory TypeFactory { get; private set; }
        public int nCalled { get; set; }
        public State globaltable { get; private set; }
        public HashSet<Name> Resolved { get; private set; }
        public HashSet<Name> unresolved { get; private set; }
        public Builtins Builtins { get; private set; }
        public State ModuleTable = new State(null, State.StateType.GLOBAL);

        /// <summary>
        /// Main entry to the analyzer
        /// </summary>
        public void Analyze(string path)
        {
            string upath = FileSystem.GetFullPath(path);
            projectDir = FileSystem.DirectoryExists(upath) ? upath : FileSystem.GetDirectoryName(upath);
            LoadFileRecursive(upath);
        }

        public bool HasOption(string option)
        {
            if (options.TryGetValue(option, out object op) && (bool) op)
                return true;
            else
                return false;
        }

        public void setOption(string option)
        {
            options[option] = true;
        }

        public void setCWD(string cd)
        {
            if (cd != null)
            {
                cwd = FileSystem.GetFullPath(cd);
            }
        }

        public void addPaths(List<string> p)
        {
            foreach (string s in p)
            {
                addPath(s);
            }
        }

        public void addPath(string p)
        {
            path.Add(FileSystem.GetFullPath(p));
        }

        public void setPath(List<string> path)
        {
            this.path = new List<string>(path.Count);
            addPaths(path);
        }

        private void AddPythonPath()
        {
            string path = Environment.GetEnvironmentVariable("PYTHONPATH");
            if (path != null)
            {
                //$BUG: Unix-specific?
                string[] segments = path.Split(':');
                foreach (string p in segments)
                {
                    addPath(p);
                }
            }
        }

        private void CopyModels()
        {
#if NOT
        URL resource = Thread.currentThread().getContextClassLoader().getResource(MODEL_LOCATION);
        string dest = _.locateTmp("models");
        this.modelDir = dest;

        try {
            _.copyResourcesRecursively(resource, new File(dest));
            _.msg("copied models to: " + modelDir);
        } catch (Exception e) {
            _.die("Failed to copy models. Please check permissions of writing to: " + dest);
        }
        addPath(dest);
#endif
        }

        public List<string> getLoadPath()
        {
            List<string> loadPath = new List<string>();
            if (cwd != null)
            {
                loadPath.Add(cwd);
            }
            if (projectDir != null && FileSystem.DirectoryExists(projectDir))
            {
                loadPath.Add(projectDir);
            }
            loadPath.AddRange(path);
            return loadPath;
        }

        public bool inStack(Exp f)
        {
            return callStack.Contains(f);
        }

        public void pushStack(Exp f)
        {
            callStack.Add(f);
        }

        public void popStack(Exp f)
        {
            callStack.Remove(f);
        }

        public bool inImportStack(object f)
        {
            return importStack.Contains(f);
        }

        public void pushImportStack(object f)
        {
            importStack.Add(f);
        }

        public void popImportStack(object f)
        {
            importStack.Remove(f);
        }

        public List<Binding> GetAllBindings()
        {
            return allBindings;
        }

        public IEnumerable<Binding> GetModuleBindings()
        {
            return ModuleTable.table.Values
                .SelectMany(g =>g)
                .Where(g => g.kind == BindingKind.MODULE && 
                            !g.IsBuiltin && !g.IsSynthetic);
        }

        ModuleType GetCachedModule(string file)
        {
            DataType t = ModuleTable.lookupType(GetModuleQname(file));
            if (t == null)
            {
                return null;
            }
            else if (t is UnionType)
            {
                foreach (DataType tt in ((UnionType) t).types)
                {
                    if (tt is ModuleType)
                    {
                        return (ModuleType) tt;
                    }
                }
                return null;
            }
            else if (t is ModuleType)
            {
                return (ModuleType) t;
            }
            else
            {
                return null;
            }
        }

        public string GetModuleQname(string file)
        {
            if (file.EndsWith("__init__.py"))
            {
                file = FileSystem.GetDirectoryName(file);
            }
            else if (file.EndsWith(suffix))
            {
                file = file.Substring(0, file.Length - suffix.Length);
            }
            return file.Replace(".", "%20").Replace('/', '.').Replace('\\', '.');
        }

        public List<Diagnostic> GetDiagnosticsForFile(string file)
        {
            if (semanticErrors.TryGetValue(file, out var errs))
            {
                return errs;
            }
            return new List<Diagnostic>();
        }

        public void putRef(Node node, ICollection<Binding> bs)
        {
            if (!(node is Url))
            {
                if (!references.TryGetValue(node, out var bindings))
                {
                    bindings = new List<Binding>(1);
                    references[node] = bindings;
                }
                foreach (Binding b in bs)
                {
                    if (!bindings.Contains(b))
                    {
                        bindings.Add(b);
                    }
                    b.addRef(node);
                }
            }
        }

        public void putRef(Node node, Binding b)
        {
            List<Binding> bs = new List<Binding> { b };
            putRef(node, bs);
        }

        public Dictionary<Node, List<Binding>> getReferences()
        {
            return references;
        }

        public void putProblem(Node loc, string msg)
        {
            string file = loc.Filename;
            if (file != null)
            {
                addFileErr(file, loc.Start, loc.End, msg);
            }
        }

        // for situations without a Node
        public void putProblem(string file, int begin, int end, string msg)
        {
            if (file != null)
            {
                addFileErr(file, begin, end, msg);
            }
        }

        void addFileErr(string file, int begin, int end, string msg)
        {
            var d = new Diagnostic(file, Diagnostic.Category.ERROR, begin, end, msg);
            getFileErrs(file, semanticErrors).Add(d);
        }

        List<Diagnostic> getFileErrs(string file, Dictionary<string, List<Diagnostic>> map)
        {
            if (!map.TryGetValue(file, out var msgs))
            {
                msgs = new List<Diagnostic>();
                map[file] = msgs;
            }
            return msgs;
        }

        public DataType LoadFile(string path)
        {
            path = FileSystem.GetFullPath(path);

            if (!FileSystem.FileExists(path))
            {
                return null;
            }

            ModuleType module = GetCachedModule(path);
            if (module != null)
            {
                return module;
            }

            // detect circular import
            if (inImportStack(path))
            {
                return null;
            }

            // set new CWD and save the old one on stack
            string oldcwd = cwd;
            setCWD(FileSystem.GetDirectoryName(path));

            pushImportStack(path);
            DataType type = parseAndResolve(path);
            popImportStack(path);

            // restore old CWD
            setCWD(oldcwd);
            return type;
        }

        private DataType parseAndResolve(string file)
        {
            loadingProgress.Tick();
            Module ast = getAstForFile(file);

            if (ast == null)
            {
                failedToParse.Add(file);
                return null;
            }
            else
            {
                DataType type = new TypeTransformer(ModuleTable, this).VisitModule(ast);
                loadedFiles.Add(file);
                return type;
            }
        }

        private void CreateCacheDirectory()
        {
            var p = FileSystem.CombinePath(FileSystem.getSystemTempDir(), "pysonar2");
            cacheDir =FileSystem.CombinePath(p, "ast_cache");
            string f = cacheDir;
            msg("AST cache is at: " + cacheDir);

            if (!FileSystem.FileExists(f))
            {
                try
                {
                    FileSystem.CreateDirectory(f);
                }
                catch (Exception ex)
                {
                    throw new ApplicationException(
                        "Failed to create tmp directory: " + cacheDir + ".", ex);
                }
            }
        }


        private AstCache GetAstCache()
        {
            if (astCache == null)
                astCache = new AstCache(this, FileSystem, logger, cacheDir);
            return astCache;
        }

        /// <summary>
        /// Returns the syntax tree for {@code file}. <p>
        /// </summary>
        public Module getAstForFile(string file)
        {
            return GetAstCache().getAST(file);
        }

        public ModuleType getBuiltinModule(string qname)
        {
            return Builtins.get(qname);
        }

        public string MakeQname(List<Name> names)
        {
            if (names.Count == 0)
            {
                return "";
            }

            string ret = "";

            for (int i = 0; i < names.Count - 1; i++)
            {
                ret += names[i].Name + ".";
            }

            ret += names[names.Count - 1].Name;
            return ret;
        }

        /// <summary>
        /// Find the path that contains modname. Used to find the starting point of locating a qname.
        /// </summary>
        /// <param name="headName">first module name segment</param>
        public string locateModule(string headName)
        {
            List<string> loadPath = getLoadPath();
            foreach (string p in loadPath)
            {
                string startDir = FileSystem.CombinePath(p, headName);
                string initFile = FileSystem.CombinePath(startDir, "__init__.py");

                if (FileSystem.FileExists(initFile))
                {
                    return p;
                }

                string startFile = FileSystem.CombinePath(startDir , suffix);
                if (FileSystem.FileExists(startFile))
                {
                    return p;
                }
            }
            return null;
        }

        public DataType LoadModule(List<Name> name, State state)
        {
            if (name.Count == 0)
            {
                return null;
            }

            string qname = MakeQname(name);
            DataType mt = getBuiltinModule(qname);
            if (mt != null)
            {
                state.Insert(
                        this,
                        name[0].Name,
                        new Url(Builtins.LIBRARY_URL + mt.Table.Path + ".html"),
                        mt, BindingKind.SCOPE);
                return mt;
            }

            // If there are more than one segment
            // load the packages first
            DataType prev = null;
            string startPath = locateModule(name[0].Name);

            if (startPath == null)
            {
                return null;
            }

            string path = startPath;
            for (int i = 0; i < name.Count; i++)
            {
                path = FileSystem.CombinePath(path, name[i].Name);
                string initFile = FileSystem.CombinePath(path, "__init__.py");
                if (FileSystem.FileExists(initFile))
                {
                    DataType mod = LoadFile(initFile);
                    if (mod == null)
                    {
                        return null;
                    }

                    if (prev != null)
                    {
                        prev.Table.Insert(this, name[i].Name, name[i], mod, BindingKind.VARIABLE);
                    }
                    else
                    {
                        state.Insert(this, name[i].Name, name[i], mod, BindingKind.VARIABLE);
                    }

                    prev = mod;

                }
                else if (i == name.Count - 1)
                {
                    string startFile = path + suffix;
                    if (FileSystem.FileExists( startFile))
                    {
                        DataType mod = LoadFile(startFile);
                        if (mod == null)
                        {
                            return null;
                        }
                        if (prev != null)
                        {
                            prev.Table.Insert(this, name[i].Name, name[i], mod, BindingKind.VARIABLE);
                        }
                        else
                        {
                            state.Insert(this, name[i].Name, name[i], mod, BindingKind.VARIABLE);
                        }
                        prev = mod;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            return prev;
        }

        /// <summary>
        /// Load all Python source files recursively if the given fullname is a
        /// directory; otherwise just load a file.  Looks at file extension to
        /// determine whether to load a given file.
        /// </summary>
        public void LoadFileRecursive(string fullname)
        {
            int count = countFileRecursive(fullname);
            if (loadingProgress == null)
            {
                loadingProgress = new Progress(this, count, 50, this.HasOption("quiet"));
            }

            string file_or_dir = fullname;

            if (FileSystem.DirectoryExists(file_or_dir))
            {
                foreach (string file in FileSystem.GetFileSystemEntries(file_or_dir))
                {
                    LoadFileRecursive(file);
                }
            }
            else
            {
                if (file_or_dir.EndsWith(suffix))
                {
                    LoadFile(file_or_dir);
                }
            }
        }

        /// <summary>
        /// Count number of .py files
        /// </summary>
        /// <param name="fullname"></param>
        /// <returns></returns>
        public int countFileRecursive(string fullname)
        {
            string file_or_dir = fullname;
            int sum = 0;

            if (FileSystem.DirectoryExists(file_or_dir))
            {
                foreach (string file in FileSystem.GetFileSystemEntries(file_or_dir))
                {
                    sum += countFileRecursive(file);
                }
            }
            else
            {
                if (file_or_dir.EndsWith(suffix))
                {
                    ++sum;
                }
            }
            return sum;
        }

        public void Finish()
        {
            msg("\nFinished loading files. " + nCalled + " functions were called.");
            msg("Analyzing uncalled functions");
            ApplyUncalled();

            // mark unused variables
            foreach (Binding b in allBindings)
            {
                if (!(b.type is ClassType) &&
                        !(b.type is FunType) &&
                        !(b.type is ModuleType)
                        && b.refs.Count == 0)
                {
                    putProblem(b.node, "Unused variable: " + b.name);
                }
            }
            msg(getAnalysisSummary());
        }

        public void close()
        {
            astCache.close();
        }

        public void msg(string m)
        {
            if (HasOption("quiet"))
            {
                Console.WriteLine(m);
            }
        }

        public void msg_(string m)
        {
            if (HasOption("quiet"))
            {
                Console.Write(m);
            }
        }

        public void AddUncalled(FunType cl)
        {
            if (cl.Definition != null && !cl.Definition.called)
            {
                uncalled.Add(cl);
            }
        }

        public void RemoveUncalled(FunType f)
        {
            uncalled.Remove(f);
        }

        public void ApplyUncalled()
        {
            IProgress progress = new Progress(this, uncalled.Count, 50, this.HasOption("quiet"));

            while (uncalled.Count != 0)
            {
                List<FunType> uncalledDup = new List<FunType>(uncalled);

                foreach (FunType cl in uncalledDup)
                {
                    progress.Tick();
                    TypeTransformer.Apply(this, cl, null, null, null, null, null);
                }
            }
        }

        public string FormatTime(TimeSpan span)
        {
            return string.Format("{0:hh\\:mm\\:ss}", span);
        }

        public string getAnalysisSummary()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.Append("\n" + banner("Analysis summary"));

            string duration = FormatTime(DateTime.Now  - this.startTime);
            sb.Append("\n- total time: " + duration);
            sb.Append("\n- modules loaded: " + loadedFiles.Count);
            sb.Append("\n- semantic problems: " + semanticErrors.Count);
            sb.Append("\n- failed to parse: " + failedToParse.Count);

            // calculate number of defs, refs, xrefs
            int nDef = 0, nXRef = 0;
            foreach (Binding b in GetAllBindings())
            {
                nDef += 1;
                nXRef += b.refs.Count;
            }

            sb.Append("\n- number of definitions: " + nDef);
            sb.Append("\n- number of cross references: " + nXRef);
            sb.Append("\n- number of references: " + getReferences().Count);

            long resolved = this.Resolved.Count;
            long unresolved = this.unresolved.Count;
            sb.Append("\n- resolved names: " + resolved);
            sb.Append("\n- unresolved names: " + unresolved);
            sb.Append("\n- name resolve rate: " + Percent(resolved, resolved + unresolved));

            return sb.ToString();
        }

        public string banner(string msg)
        {
            return "---------------- " + msg + " ----------------";
        }

        public List<string> getLoadedFiles()
        {
            List<string> files = new List<string>();
            foreach (string file in loadedFiles)
            {
                if (file.EndsWith(suffix))
                {
                    files.Add(file);
                }
            }
            return files;
        }

        public void RegisterBinding(Binding b)
        {
            allBindings.Add(b);
        }

        public override string ToString()
        {
            return "(analyzer:" +
                    "[" + allBindings.Count + " bindings] " +
                    "[" + references.Count + " refs] " +
                    "[" + loadedFiles.Count + " files] " +
                    ")";
        }

        /// <summary>
        /// Given an absolute {@code path} to a file (not a directory), 
        /// returns the module name for the file.  If the file is an __init__.py, 
        /// returns the last component of the file's parent directory, else 
        /// returns the filename without path or extension. 
        /// </summary>
        public string ModuleName(string path)
        {
            string f = path;
            string name = FileSystem.GetFileName(f);
            if (name == "__init__.py")
            {
                return FileSystem.GetDirectoryName(f);
            }
            else if (name.EndsWith(suffix))
            {
                return name.Substring(0, name.Length - suffix.Length);
            }
            else
            {
                return name;
            }
        }

        public Binding CreateBinding(string id, Node node, DataType type, BindingKind kind)
        {
            var b = new Binding(id, node, type, kind);
            RegisterBinding(b);
            return b;
        }

        public string Percent(long num, long total)
        {
            if (total == 0)
            {
                return "100%";
            }
            else
            {
                int pct = (int) (num * 100 / total);
                return string.Format("{0:3}%", pct);
            }
        }

        public void addRef(AttributeAccess attr, DataType targetType, ISet<Binding> bs)
        {
            foreach (Binding b in bs)
            {
                putRef(attr, b);
                if (attr.Parent != null && attr.Parent is Application &&
                        b.type is FunType && targetType is InstanceType)
                {  // method call 
                    ((FunType) b.type).SelfType = targetType;
                }
            }
        }

        public string ExtendPath(string path, string name)
        {
            name = ModuleName(name);
            if (path == "")
            {
                return name;
            }
            return path + "." + name;
        }
    }
}
