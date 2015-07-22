﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pytocs.Syntax;
using Pytocs.Types;
using Name = Pytocs.Syntax.Identifier;

namespace Pytocs.TypeInference
{
    public interface Analyzer
    {
        IFileSystem FileSystem { get; }

        DataType loadFile(string path);
        DataType loadModule(List<Name> name, State state);
        Module getAstForFile(string file);
        string moduleQname(string file);

        DataTypeFactory TypeFactory { get; }
        Binding CreateBinding(string id, Node node, DataType type, Binding.Kind kind);
        void addRef(AttributeAccess attr, DataType targetType, ISet<Binding> bs);
        void putRef(Node node, ICollection<Binding> bs);
        void putRef(Node node, Binding bs);
        void addUncalled(FunType f);
        void removeUncalled(FunType f);
        int nCalled { get; set; }
        void pushStack(object v);
        void popStack(object v);
        bool inStack(object v);
        State globaltable { get; }
        HashSet<Name> resolved { get; }
        HashSet<Name> unresolved { get; }

        string moduleName(string path);
        string extendPath(string path, string name);

        void putProblem(Node loc, string msg);
        void putProblem(string filename, int start, int end, string msg);

        void msg(string message);
        void msg_(string message);
        string formatNumber(object n, int length);
        string percent(long num, long total);
    }

    public class AnalyzerImpl : Analyzer
    {

        public const string MODEL_LOCATION = "org/yinwang/pysonar/models";

        // global static instance of the analyzer itself
        public static Analyzer self;
        public State moduleTable = new State(null, State.StateType.GLOBAL);
        public List<string> loadedFiles = new List<string>();
        public List<Binding> allBindings = new List<Binding>();
        private Dictionary<Node, List<Binding>> references = new Dictionary<Node, List<Binding>>();  // new LinkedHashMap<>();
        public Dictionary<string, List<Diagnostic>> semanticErrors = new Dictionary<string, List<Diagnostic>>();
        public Dictionary<string, List<Diagnostic>> parseErrors = new Dictionary<string, List<Diagnostic>>();
        public string cwd = null;
        public bool multilineFunType = false;
        public List<string> path = new List<string>();
        private HashSet<FunType> uncalled = new HashSet<FunType>();
        private HashSet<object> callStack = new HashSet<object>();
        private HashSet<object> importStack = new HashSet<object>();

        private AstCache astCache;
        public String cacheDir;
        public HashSet<string> failedToParse = new HashSet<string>();
        public Statistics stats = new Statistics();
        public Builtins builtins;
        private Progress loadingProgress = null;

        public String projectDir;
        public String modelDir;
        public String suffix;

        public Dictionary<string, Object> options;
        private DateTime startTime;


        public AnalyzerImpl()
            : this(new FileSystem(), new Dictionary<string, object>(), DateTime.Now)
        {
        }

        public AnalyzerImpl(IFileSystem fs, Dictionary<string, object> options, DateTime startTime)
        {
            this.FileSystem = fs;
            this.TypeFactory = new DataTypeFactory(this);
            this.globaltable = new State(null, State.StateType.GLOBAL);
            this.resolved = new HashSet<Name>();
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
            this.builtins = new Builtins(this);
            this.builtins.init();
            addPythonPath();
            copyModels();
            createCacheDir();
            getAstCache();
        }

        public IFileSystem FileSystem { get; private set; }
        public DataTypeFactory TypeFactory { get; private set; }
        public int nCalled { get; set; }
        public State globaltable { get; private set; }
        public HashSet<Name> resolved { get; private set; }
        public HashSet<Name> unresolved { get; private set; }

        public bool hasOption(string option)
        {
            object op;
            if (options.TryGetValue(option, out op) && (bool) op)
                return true;
            else
                return false;
        }


        public void setOption(string option)
        {
            options[option] = true;
        }


        // main entry to the analyzer
        public void Analyze(string path)
        {
            string upath = FileSystem.GetFullPath(path);
            projectDir = FileSystem.DirectoryExists(upath) ? upath : FileSystem.GetDirectoryName(upath);
            loadFileRecursive(upath);
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


        private void addPythonPath()
        {
            string path = Environment.GetEnvironmentVariable("PYTHONPATH");
            if (path != null)
            {
                string[] segments = path.Split(':');
                foreach (string p in segments)
                {
                    addPath(p);
                }
            }
        }


        private void copyModels()
        {
#if NOT
        URL resource = Thread.currentThread().getContextClassLoader().getResource(MODEL_LOCATION);
        String dest = _.locateTmp("models");
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


        public bool inStack(object f)
        {
            return callStack.Contains(f);
        }


        public void pushStack(object f)
        {
            callStack.Add(f);
        }


        public void popStack(object f)
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


        public List<Binding> getAllBindings()
        {
            return allBindings;
        }


        ModuleType getCachedModule(string file)
        {
            DataType t = moduleTable.lookupType(moduleQname(file));
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

        public string moduleQname(string file)
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


        public List<Diagnostic> getDiagnosticsForFile(string file)
        {
            List<Diagnostic> errs;
            if (semanticErrors.TryGetValue(file, out errs))
            {
                return errs;
            }
            return new List<Diagnostic>();
        }


        public void putRef(Node node, ICollection<Binding> bs)
        {
            if (!(node is Url))
            {
                List<Binding> bindings;
                if (!references.TryGetValue(node, out bindings))
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
            List<Binding> bs = new List<Binding>();
            bs.Add(b);
            putRef(node, bs);
        }

        public Dictionary<Node, List<Binding>> getReferences()
        {
            return references;
        }


        public void putProblem(Node loc, string msg)
        {
            String file = loc.Filename;
            if (file != null)
            {
                addFileErr(file, loc.Start, loc.End, msg);
            }
        }


        // for situations without a Node
        public void putProblem(string file, int begin, int end, String msg)
        {
            if (file != null)
            {
                addFileErr(file, begin, end, msg);
            }
        }


        void addFileErr(String file, int begin, int end, String msg)
        {
            Diagnostic d = new Diagnostic(file, Diagnostic.Category.ERROR, begin, end, msg);
            getFileErrs(file, semanticErrors).Add(d);
        }


        List<Diagnostic> getFileErrs(string file, Dictionary<string, List<Diagnostic>> map)
        {
            List<Diagnostic> msgs;
            if (!map.TryGetValue(file, out msgs))
            {
                msgs = new List<Diagnostic>();
                map[file] = msgs;
            }
            return msgs;
        }

        public DataType loadFile(string path)
        {
            path = FileSystem.GetFullPath(path);

            if (!FileSystem.FileExists(path))
            {
                return null;
            }

            ModuleType module = getCachedModule(path);
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
            String oldcwd = cwd;
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
            loadingProgress.tick();
            Module ast = getAstForFile(file);

            if (ast == null)
            {
                failedToParse.Add(file);
                return null;
            }
            else
            {
                DataType type = new TypeTransformer(moduleTable, this).VisitModule(ast);
                loadedFiles.Add(file);
                return type;
            }
        }


        private void createCacheDir()
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


        private AstCache getAstCache()
        {
            if (astCache == null)
                astCache = new AstCache(this, FileSystem, new Logger("@"), cacheDir);
            return astCache;
        }

        /// <summary>
        /// Returns the syntax tree for {@code file}. <p>
        /// </summary>
        public Module getAstForFile(string file)
        {
            return getAstCache().getAST(file);
        }

        public ModuleType getBuiltinModule(String qname)
        {
            return builtins.get(qname);
        }

        public String makeQname(List<Name> names)
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



        public DataType loadModule(List<Name> name, State state)
        {
            if (name.Count == 0)
            {
                return null;
            }

            String qname = makeQname(name);

            DataType mt = getBuiltinModule(qname);
            if (mt != null)
            {
                state.insert(
                        this,
                        name[0].Name,
                        new Url(Builtins.LIBRARY_URL + mt.Table.Path + ".html"),
                        mt, Binding.Kind.SCOPE);
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
                    DataType mod = loadFile(initFile);
                    if (mod == null)
                    {
                        return null;
                    }

                    if (prev != null)
                    {
                        prev.Table.insert(this, name[i].Name, name[i], mod, Binding.Kind.VARIABLE);
                    }
                    else
                    {
                        state.insert(this, name[i].Name, name[i], mod, Binding.Kind.VARIABLE);
                    }

                    prev = mod;

                }
                else if (i == name.Count - 1)
                {
                    string startFile = path + suffix;
                    if (FileSystem.FileExists( startFile))
                    {
                        DataType mod = loadFile(startFile);
                        if (mod == null)
                        {
                            return null;
                        }
                        if (prev != null)
                        {
                            prev.Table.insert(this, name[i].Name, name[i], mod, Binding.Kind.VARIABLE);
                        }
                        else
                        {
                            state.insert(this, name[i].Name, name[i], mod, Binding.Kind.VARIABLE);
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
        public void loadFileRecursive(string fullname)
        {
            int count = countFileRecursive(fullname);
            if (loadingProgress == null)
            {
                loadingProgress = new Progress(this, count, 50, this.hasOption("quiet"));
            }

            string file_or_dir = fullname;

            if (FileSystem.DirectoryExists(file_or_dir))
            {
                foreach (string file in FileSystem.GetFileSystemEntries(file_or_dir))
                {
                    loadFileRecursive(file);
                }
            }
            else
            {
                if (file_or_dir.EndsWith(suffix))
                {
                    loadFile(file_or_dir);
                }
            }
        }


        // count number of .py files
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


        public void finish()
        {
            msg("\nFinished loading files. " + nCalled + " functions were called.");
            msg("Analyzing uncalled functions");
            applyUncalled();

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
            if (hasOption("quiet"))
            {
                Console.WriteLine(m);
            }
        }




        public void msg_(string m)
        {
            if (hasOption("quiet"))
            {
                Console.Write(m);
            }
        }


        public void addUncalled(FunType cl)
        {
            if (!cl.Definition.called)
            {
                uncalled.Add(cl);
            }
        }


        public void removeUncalled(FunType f)
        {
            uncalled.Remove(f);
        }


        public void applyUncalled()
        {
            Progress progress = new Progress(this, uncalled.Count, 50, this.hasOption("quiet"));

            while (uncalled.Count != 0)
            {
                List<FunType> uncalledDup = new List<FunType>(uncalled);

                foreach (FunType cl in uncalledDup)
                {
                    progress.tick();
                    TypeTransformer.apply(this, cl, null, null, null, null, null);
                }
            }
        }


        public string formatTime(TimeSpan span)
        {
            return string.Format("{0:hh\\:mm\\:ss}", span);
        }

        public String getAnalysisSummary()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.Append("\n" + banner("analysis summary"));

            String duration = formatTime(DateTime.Now  - this.startTime);
            sb.Append("\n- total time: " + duration);
            sb.Append("\n- modules loaded: " + loadedFiles.Count);
            sb.Append("\n- semantic problems: " + semanticErrors.Count);
            sb.Append("\n- failed to parse: " + failedToParse.Count);

            // calculate number of defs, refs, xrefs
            int nDef = 0, nXRef = 0;
            foreach (Binding b in getAllBindings())
            {
                nDef += 1;
                nXRef += b.refs.Count;
            }

            sb.Append("\n- number of definitions: " + nDef);
            sb.Append("\n- number of cross references: " + nXRef);
            sb.Append("\n- number of references: " + getReferences().Count);

            long resolved = this.resolved.Count;
            long unresolved = this.unresolved.Count;
            sb.Append("\n- resolved names: " + resolved);
            sb.Append("\n- unresolved names: " + unresolved);
            sb.Append("\n- name resolve rate: " + percent(resolved, resolved + unresolved));

            return sb.ToString();
        }

        public string banner(string msg)
        {
            return "---------------- " + msg + " ----------------";
        }

        public List<String> getLoadedFiles()
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


        public void registerBinding(Binding b)
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
        public string moduleName(string path)
        {
            string f = path;
            String name = FileSystem.GetFileName(f);
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

        public Binding CreateBinding(string id, Node node, DataType type, Binding.Kind kind)
        {
            var b = new Binding(id, node, type, kind);
            registerBinding(b);
            return b;
        }


        public string percent(long num, long total)
        {
            if (total == 0)
            {
                return "100%";
            }
            else
            {
                int pct = (int) (num * 100 / total);
                return String.Format("{0:3}", pct) + "%";
            }
        }

        /// <summary>
        /// format number with fixed width 
        /// </summary>
        public string formatNumber(object n, int length)
        {
            if (length == 0)
            {
                length = 1;
            }
            if (n is int)
            {
                return String.Format("{0:" + length + "}", (int) n);
            }
            else if (n is long)
            {
                return String.Format("{0:" + length + "}", (long) n);
            }
            else
            {
                return String.Format("{0:" + length + "}", n);
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

        public string extendPath(string path, string name)
        {
            name = moduleName(name);
            if (path == "")
            {
                return name;
            }
            return path + "." + name;
        }
    }
}
