﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Pytocs.TypeInference
{
    public interface IFileSystem
    {
        string DirectorySeparatorChar { get; }

        void CreateDirectory(string f);
        TextReader CreateStreamReader(string filename);
        void DeleteDirectory(string directory);
        string CombinePath(string dir, string file);
        bool DirectoryExists(string filePath);
        bool FileExists(string filePath);
        string GetDirectoryName(string filePath);
        string getFileHash(string path);
        string [] GetFileSystemEntries(string file_or_dir);
        string getSystemTempDir();
        string GetFileName(string path);
        string makePathString(params string[] files);
        byte[] ReadFileBytes(string path);
        string ReadFile(string path);
        string relPath(string path1, string path2);
        string GetFullPath(string file);
        void WriteFile(string path, string contents);
        void DeleteFile(string path);
    }

    public class FileSystem : IFileSystem
    {
        public string DirectorySeparatorChar { get { return new string(Path.DirectorySeparatorChar, 1); } }

        public void CreateDirectory(string directory) { Directory.CreateDirectory(directory); }

        public TextReader CreateStreamReader(string filename) { return new StreamReader(filename); }

        public void DeleteDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                string[] files = Directory.GetFileSystemEntries(directory);
                if (files != null)
                {
                    foreach (string f in files)
                    {
                        if (Directory.Exists(f))
                        {
                            DeleteDirectory(f);
                        }
                        else
                        {
                            File.Delete(f);
                        }
                    }
                }
                Directory.Delete(directory);
            }
        }

        public void DeleteFile(String path)
        {
            File.Delete(path);
        }

        public string CombinePath(string dir, string file)
        {
            return Path.Combine(Path.GetFullPath(dir), file);
        }

        public bool DirectoryExists(string dirPath) { return Directory.Exists(dirPath); }
        public bool FileExists(string filePath) { return File.Exists(filePath); }

        public string getSystemTempDir()
        {
            String tmp = Environment.GetEnvironmentVariable("TEMP");
            var sep = DirectorySeparatorChar;
            if (tmp.EndsWith(sep + ""))
            {
                return tmp;
            }
            return tmp + sep;
        }

        public string GetDirectoryName(string filePath) { return Path.GetDirectoryName(filePath); }
        public string GetFileName(string filePath) { return Path.GetFileName(filePath); }
        public string[] GetFileSystemEntries(string dirPath) { return Directory.GetFileSystemEntries(dirPath); } 
        
        public string makePathString(params string[] files)
        {
            return Path.Combine(files);
        }

        public string getFileHash(string path)
        {
            byte[] bytes = ReadFileBytes(path);
            return getContentHash(Encoding.UTF8.GetBytes(path)) + "." + getContentHash(bytes);
        }

        public static string getContentHash(byte[] fileContents)
        {
            HashAlgorithm algorithm = new SHA1Managed();
            byte[] messageDigest = algorithm.ComputeHash(fileContents);
            StringBuilder sb = new StringBuilder();
            foreach (byte aMessageDigest in messageDigest)
            {
                sb.Append(String.Format("{0:X2}", 0xFF & aMessageDigest));
            }
            return sb.ToString();
        }

        public string ReadFile(string path)
        {
            // Don't use line-oriented file read -- need to retain CRLF if present
            // so the style-run and link offsets are correct.
            byte[] content;
            try
            {
                content = File.ReadAllBytes(path);
                return Encoding.UTF8.GetString(content);
            }
            catch
            {
                return null;
            }
        }

        public byte[] ReadFileBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        public string relPath(string path1, string path2)
        {
            string a = GetFullPath(path1);
            string b = GetFullPath(path2);

            string[] aSegments = a.Split('/', '\\');
            string[] bSegments = b.Split('/', '\\');

            int i;
            for (i = 0; i < Math.Min(aSegments.Length, bSegments.Length); i++)
            {
                if (!aSegments[i].Equals(bSegments[i]))
                {
                    break;
                }
            }

            int ups = aSegments.Length - i - 1;
            string res = null;
            for (int x = 0; x < ups; x++)
            {
                res = res + Path.DirectorySeparatorChar + "..";
            }

            for (int y = i; y < bSegments.Length; y++)
            {
                res = res + bSegments[y];
            }

            if (res == null)
            {
                return null;
            }
            else
            {
                return res;
            }
        }

        public string GetFullPath(string file)
        {
            return Path.GetFullPath(file);
        }

        public void WriteFile(string path, string contents)
        {
            using (TextWriter output = new StreamWriter(path))
            {
                output.Write(contents);
                output.Flush();
            }
        }
    }
}
