﻿using scalus.Dto;
using scalus.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using scalus.Util;
using Serilog;
using static scalus.Dto.ParserConfigDefinitions;

namespace scalus.UrlParser
{
    public abstract class BaseParser : IUrlParser
    {  
        private string _fileProcessorExe = null;
        private List<string> _fileProcessorArgs = null;       
        protected string FileExtension { get; set; } = ".scalus";
        protected IDictionary<Token, string> Dictionary { get; set; } = DefaultDictionary();
        
        protected static IDictionary<Token, string> DefaultDictionary()
        {
            var dictionary = new Dictionary<Token, string>();
            foreach (var one in Enum.GetValues(typeof(Token)))
            {
                dictionary[(Token)one] = string.Empty;
            }

            return dictionary;
        }
        public Regex SafeguardUserPattern = new Regex(
           @"(vaultaddress[=|~]([^@%]+)[@|%]token[~|=]([^@%]+)[@|%]([^@%]+)[@|%](.*))", RegexOptions.IgnoreCase);

        protected ParserConfig Config { get; }
          
        
        protected CompositeDisposable Disposables { get; } = new CompositeDisposable();

       
        public BaseParser( ParserConfig config)
        {
            Config = config;
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public virtual void PreExecute(IOsServices services)
        {
            if (string.IsNullOrEmpty(_fileProcessorExe ))
                return;
            Log.Debug($"Starting file preprocessor: '{_fileProcessorExe}' with args: '{string.Join(' ', _fileProcessorArgs)}'");

            if (!File.Exists(_fileProcessorExe ))
            {
                Log.Error($"Selected file preprocessor does not exist:{_fileProcessorExe}");
                return;
            }
            string output;
            string err;
            var res = services.Execute(_fileProcessorExe, _fileProcessorArgs, out output, out err);
            Log.Information($"File preprocess result:{res}, output:{output}, err:{err}");
            
        }

        public virtual void PostExecute(Process process)
        {
            var time = 0;
            if (Config.Options == null || Config.Options.Count == 0)
            {
                time = 10;
            }
            else {
                if(Config.Options.Any(x => string.Equals(x, ProcessingOptions.waitforexit.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information($"post processing - wait for exit");
                    process.WaitForExit();
                }
                else if (Config.Options.Any(x => string.Equals(x, ProcessingOptions.waitforinputidle.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information($"post processing - wait for inputidle");
                    process.WaitForInputIdle();
                }
                else
                {
                    time = 10;
                    var wait = Config.Options.FirstOrDefault(x =>
                        x.StartsWith($"{ProcessingOptions.wait}", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(wait))
                    {
                        if (wait.Equals($"{ProcessingOptions.wait}", StringComparison.OrdinalIgnoreCase))
                        {
                            time = 0;
                        }
                        else
                        {
                            var parts = wait.Split(":");
                            if (parts.Length > 1)
                            {
                                int.TryParse(parts[1], out time);
                            }
                        }
                    }
                }
            }
            if (time > 0)
            {
                Log.Information($"post processing - waiting for {time} seconds");
                Task.Delay(time * 1000).Wait();
            }

            if (process.HasExited)
            {
                Log.Information($"Application exited with exit code: {process.ExitCode}");
            }
            else
            {
                Log.Information($"Application still running - scalus has finished");
            }
        }

        public abstract IDictionary<Token, string> Parse(string url);

        protected void SetValue(Match match, int index, Token property, bool decode, string defValue = null)
        {
            var val = defValue??string.Empty;
            if (match.Success && match.Groups.Count >= index)
            {
                if (!string.IsNullOrEmpty(match.Groups[index].Value))
                {
                    val = match.Groups[index].Value;
                }
            }
            if (decode)
            {
                val = HttpUtility.UrlDecode(val);
            }
            Dictionary[property] = val;
        }
        
        protected void GetSafeguardUserValue()
        {
            var match = SafeguardUserPattern.Match(Dictionary[Token.User]);
           
            SetValue(match, 2, Token.Vault, false);
            SetValue(match, 3, Token.Token,false );
            SetValue(match, 4, Token.TargetUser, false);
            
            SetValue(match, 5, Token.TargetHost, false);
            (string host, string port) = ParseHost(Dictionary[Token.TargetHost]);
            Dictionary[Token.TargetHost] = host;
            Dictionary[Token.TargetPort] = port;
         }

        private void WriteTempFile(IEnumerable<string> lines, string ext)
        {
            try
            {
                string tempFile;
                var isSafeguard = Dictionary.ContainsKey(Token.Vault) && !string.IsNullOrEmpty(Dictionary[Token.Vault]);
                if ( isSafeguard)
                {
                    var guid = Guid.NewGuid().ToString();
                    var host = Dictionary[Token.TargetHost];
                    host = Regex.Replace(host, "[.]", "~");
                    var user = Dictionary[Token.TargetUser];
                    user = user.Replace('\\', '~');
                    tempFile = Path.Combine(Path.GetTempPath(),
                        $"SG-{host}_{user}_{guid}{ext}");
                }
                else
                {
                    var host = (Dictionary.ContainsKey(Token.Host) && !string.IsNullOrEmpty(Dictionary[Token.Host])
                        ? Dictionary[Token.Host]
                        : string.Empty);
                    var user = (Dictionary.ContainsKey(Token.User) &&
                                !string.IsNullOrEmpty(Dictionary[Token.User])
                        ? Dictionary[Token.User]
                        : string.Empty);
                    if (!string.IsNullOrEmpty(host) || !string.IsNullOrEmpty(user))
                    {
                        var guid = Guid.NewGuid().ToString();
                        host = Regex.Replace(host, "[.]", "~");
                        user = user.Replace('\\', '~');

                        tempFile = Path.Combine(Path.GetTempPath(),
                        $"Scalus-{host}_{user}_{guid}{ext}");
                    }
                    else
                    {
                        tempFile = Path.GetTempFileName();
                        string renamed = Path.ChangeExtension(tempFile, ext);
                        File.Move(tempFile, renamed);
                        tempFile = renamed;
                    }
                }
                Disposables.Add(Disposable.Create(() => File.Delete(tempFile)));
                var newlines = new List<string>();
                foreach (var line in lines)
                {
                    var newline = line;
                    foreach (var onevar in Dictionary)
                    {
                        newline = Regex.Replace(newline, $"%{onevar.Key}%", $"{onevar.Value}", RegexOptions.IgnoreCase);
                    }

                    newlines.Add(newline);
                }
                var dir = Path.GetDirectoryName(tempFile);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(tempFile, string.Join(Environment.NewLine, newlines));
                Dictionary[Token.GeneratedFile] = tempFile;
                _fileProcessorArgs = new List<string>();
                _fileProcessorExe = string.Empty;
                if (!string.IsNullOrEmpty(Config.PostProcessingExec))
                {
                    var found = false;
                    _fileProcessorExe = ReplaceTokens(Config.PostProcessingExec);
                    foreach (var arg in Config.PostProcessingArgs)
                    {
                        if (arg.Contains($"%{Token.GeneratedFile}%"))
                        {
                            found = true;
                        }

                        _fileProcessorArgs.Add(ReplaceTokens(arg));
                    }

                    if (!found)
                    {
                        _fileProcessorArgs.Add(Dictionary[Token.GeneratedFile]);
                    }
                }

                Log.Information(
                    $"Preprocessing cmd:{_fileProcessorExe} args:{string.Join(',', _fileProcessorArgs)}");
            }
            catch (Exception e)
            {
                Log.Error($"Failed to process temp file: {e.Message}");
            }
        }
    

        protected (string host,string port) ParseHost(string host)
        {
            var sep = host.LastIndexOf(":", StringComparison.Ordinal);
            if (sep == -1)
            {
                return (host, null);
            }
            return (host.Substring(0, sep), host.Substring(sep+1));
        }
        protected static string StripProtocol(string url)
        {
            var protocolIndex = url.IndexOf("://", StringComparison.Ordinal);
            if (protocolIndex == -1) return url;
            return  url.Substring(protocolIndex + 3);
        }
        protected static string Protocol(string url, string def = null)
        {
            var protocolIndex = url.IndexOf("://", StringComparison.Ordinal);
            if (protocolIndex == -1) return def;
            return url.Substring(0, protocolIndex);
        }

        protected abstract IEnumerable<string> GetDefaultTemplate();
    
        public string ReplaceTokens(string line)
        {
            var newline = line;
            foreach (var variable in Dictionary)
            {
                // TODO: Make this more robust. Edge case escapes don't work.
                newline = Regex.Replace(newline, $"%{variable.Key}%", variable.Value??string.Empty, RegexOptions.IgnoreCase);
            }
            return newline;
        }
        public string GetFullPath(string path)
        {
            if (Path.IsPathFullyQualified(path))
            {
                return path;
            }
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, path);
        }
        protected void ParseConfig()
        {
            Dictionary[Token.Home]= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Dictionary[Token.AppData] = ConfigurationManager.ProdAppPath;
            Dictionary[Token.TempPath]= Path.GetTempPath();
            IEnumerable<string> fileLines = null;
            if (Config.UseDefaultTemplate)
            {
                Log.Information("Using default template");
                fileLines = GetDefaultTemplate();
            }
            else if (!string.IsNullOrEmpty(Config.UseTemplateFile))
            {
                Log.Information($"Using template :{Config.UseTemplateFile}");
                var templatefile = ReplaceTokens(Config.UseTemplateFile);
                templatefile = GetFullPath(templatefile);
                Log.Information($"Using template file:{templatefile}");

                if (!File.Exists(templatefile))
                {
                    Log.Error($"Application template does not exist:{templatefile}");
                    throw new Exception($"Application template file does not exist: {templatefile}");
                }
                
                var ext = Path.GetExtension(templatefile);
                if (!string.IsNullOrEmpty(ext))
                {
                    FileExtension = ext;
                }
                try {
                    fileLines = File.ReadAllLines(templatefile);
                }
                catch (Exception e)
                {
                    Log.Error(e, $"Cannot read template file: {templatefile}");
                }
            }
            if (fileLines != null)
            {
                WriteTempFile(fileLines, FileExtension);           
            }
        }

        protected void Parse(Uri url)
        {
            Dictionary = new Dictionary<Token, string>();
            try {
                Dictionary[Token.OriginalUrl]=url.GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped);
                Dictionary[Token.RelativeUrl] = StripProtocol(Dictionary[Token.OriginalUrl]);
                Dictionary[Token.Protocol] = url.GetComponents(UriComponents.Scheme, UriFormat.SafeUnescaped);
                Dictionary[Token.Host] = url.GetComponents(UriComponents.Host, UriFormat.SafeUnescaped);
                Dictionary[Token.Port] = url.GetComponents(UriComponents.Port, UriFormat.SafeUnescaped);
                Dictionary[Token.Path] = url.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped);
                Dictionary[Token.User] = url.GetComponents(UriComponents.UserInfo, UriFormat.SafeUnescaped);
                Dictionary[Token.Query] = url.GetComponents(UriComponents.Query, UriFormat.SafeUnescaped);
                Dictionary[Token.Fragment] = url.GetComponents(UriComponents.Fragment, UriFormat.SafeUnescaped);
                ParseConfig();
            }
            catch
            {
                Log.Warning($"The string does not appear to be a valid URL: {url} ");
            }
        }
       

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Disposables.Dispose();
                }

                _disposedValue = true;
            }
        }

        public List<string> ReplaceTokens(List<string> args)
        {
            var newargs = new List<string>();
            foreach (var arg in args)
            {
                var newarg = ReplaceTokens(arg.Trim());
                newargs.Add(newarg);
            }
            return newargs;
        }

        private bool _disposedValue;
    }
}
