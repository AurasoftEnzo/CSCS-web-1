﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SplitAndMerge
{
    // Returns process info
    class PsInfoFunction : ParserFunction, IStringFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();
            Utils.CheckArgs(args.Count, 1, m_name, true);
            string pattern = args[0].AsString();

            int MAX_PROC_NAME = 26;
            InterpreterInstance.AppendOutput(Utils.GetLine(), true);
            InterpreterInstance.AppendOutput(String.Format("{0} {1} {2} {3} {4} {5}",
              "Process Id".PadRight(15), "Process Name".PadRight(MAX_PROC_NAME),
              "Working Set".PadRight(15), "Virt Mem".PadRight(15),
              "Start Time".PadRight(15), "CPU Time".PadRight(25)), true);

            Process[] processes = Process.GetProcessesByName(pattern);
            List<Variable> results = new List<Variable>(processes.Length);
            for (int i = 0; i < processes.Length; i++)
            {
                Process pr = processes[i];
                int workingSet = (int)(((double)pr.WorkingSet64) / 1000000.0);
                int virtMemory = (int)(((double)pr.VirtualMemorySize64) / 1000000.0);
                string procTitle = pr.ProcessName + " " + pr.MainWindowTitle.Split(null)[0];
                string startTime = pr.StartTime.ToString();
                if (procTitle.Length > MAX_PROC_NAME)
                {
                    procTitle = procTitle.Substring(0, MAX_PROC_NAME);
                }
                string procTime = string.Empty;
                try
                {
                    procTime = pr.TotalProcessorTime.ToString().Substring(0, 11);
                }
                catch (Exception) { }

                results.Add(new Variable(
                  string.Format("{0,15} {1," + MAX_PROC_NAME + "} {2,15} {3,15} {4,15} {5,25}",
                    pr.Id, procTitle,
                    workingSet, virtMemory, startTime, procTime)));
                InterpreterInstance.AppendOutput(results.Last().String, true);
            }
            InterpreterInstance.AppendOutput(Utils.GetLine(), true);

            return new Variable(results);
        }
    }

    // Kills a process with specified process id
    class KillFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();
            Utils.CheckArgs(args.Count, 1, m_name, true);

            Variable id = args[0];
            Utils.CheckPosInt(id, script);

            int processId = (int)id.Value;
            try
            {
                Process process = Process.GetProcessById(processId);
                process.Kill();
                InterpreterInstance.AppendOutput("Process " + processId + " killed", true);
            }
            catch (Exception exc)
            {
                throw new ArgumentException($"Couldn't kill process {processId} ({exc.Message})", exc);
            }

            return Variable.EmptyInstance;
        }
    }

    // Starts running a new process, returning its ID
    class RunFunction : ParserFunction, INumericFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string processName = Utils.GetItem(script).String;
            Utils.CheckNotEmpty(processName, "processName");

            List<string> args = Utils.GetFunctionArgs(script);
            int processId = -1;

            try
            {
                Process pr = Process.Start(processName, string.Join("", args.ToArray()));
                processId = pr.Id;
            }
            catch (Exception exc)
            {
                throw new ArgumentException($"Couldn't start [{processName}]: {exc.Message}", exc);
            }

            return new Variable(processId);
        }
    }

    class ServerSocket : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();

            Utils.CheckArgs(args.Count, 2, Constants.CONNECTSRV);
            Utils.CheckPosInt(args[1], script);

            string functionToRun = Utils.GetSafeString(args, 0);
            int port = Utils.GetSafeInt(args, 1);

            var customFunction = InterpreterInstance.GetFunction(functionToRun) as CustomFunction;
            Utils.CheckNotNull(customFunction, functionToRun, script);

            InterpreterInstance.AppendOutput("Starting server with function " + functionToRun, true);

            try
            {
                IPHostEntry ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());
                IPAddress ipAddress = ipHostInfo.AddressList[0];
                IPEndPoint localEndPoint = new IPEndPoint(ipAddress, port);

                Socket listener = new Socket(AddressFamily.InterNetwork,
                                    SocketType.Stream, ProtocolType.Tcp);

                listener.Bind(localEndPoint);
                listener.Listen(10);

                Socket handler = null;
                while (true)
                {
                    InterpreterInstance.AppendOutput("Waiting for connections on " + port + " ...", true);
                    handler = listener.Accept();

                    var request = ReceiveMessage(handler);
                    SendMessage(handler, "OK");
                    var load = ReceiveMessage(handler);
                    var objReceived = MarshalFunction.Unmarshal(load, InterpreterInstance);

                    var funcArgs = new List<Variable>() { new Variable(request), objReceived };
                    var retValue = customFunction.Run(funcArgs, script);
                    var classInst = retValue.Object as CSCSClass.ClassInstance;
                    var objName = classInst != null && !string.IsNullOrWhiteSpace(classInst.InstanceName) ?
                        classInst.InstanceName : retValue.ParamName;
                    var retLoad = string.IsNullOrWhiteSpace(objName) ?
                        retValue.Marshal("") : MarshalFunction.Marshal(objName, InterpreterInstance);
                    SendMessage(handler, retLoad);

                    InterpreterInstance.AppendOutput("Finished processing client: [" + retValue.AsString() + "]", true);
                    if (request.Contains("<EOF>"))
                    {
                        break;
                    }
                }

                if (handler != null)
                {
                    handler.Shutdown(SocketShutdown.Both);
                    handler.Close();
                    listener.Close();
                }
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't start server: (" + exc.Message + ")", exc);
            }

            return Variable.EmptyInstance;
        }

        public static string ReceiveMessage(Socket handler)
        {
            var bytes = new byte[128];
            int bytesRec = handler.Receive(bytes);
            string dataSize = Encoding.UTF8.GetString(bytes, 0, bytesRec);
            if (!int.TryParse(dataSize, out int size) || size < 1)
            {
                Interpreter.LastInstance.AppendOutput("Received no valid data size: [" +
                    dataSize + "]", true);
                return "";
            }

            var msg = Encoding.UTF8.GetBytes("OK");
            handler.Send(msg);

            bytes = new byte[size];
            bytesRec = handler.Receive(bytes);
            string received = Encoding.UTF8.GetString(bytes, 0, bytesRec);

            Interpreter.LastInstance.AppendOutput("Received from " + handler.RemoteEndPoint.ToString() +
              ": [" + received + "]", true);
            return received;
        }

        public static void SendMessage(Socket handler, string msgToSend)
        {
            var len = msgToSend.Length + 1;
            var msg = Encoding.UTF8.GetBytes("" + len);
            handler.Send(msg);

            var bytes = new byte[128];
            int bytesRec = handler.Receive(bytes, bytes.Length, SocketFlags.None);

            msg = Encoding.UTF8.GetBytes(msgToSend);
            handler.Send(msg);
        }
    }

    // Starts running an "echo" client
    class ClientSocket : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();

            Utils.CheckArgs(args.Count, 3, Constants.CONNECTSRV);
            Utils.CheckPosInt(args[2], script);

            string request = Utils.GetSafeString(args, 0);
            string load = Utils.GetSafeString(args, 1);
            int port = Utils.GetSafeInt(args, 2);
            string host = Utils.GetSafeString(args, 3);

            var objName = string.IsNullOrWhiteSpace(args[1].ParamName) ? load :
                                 args[1].ParamName;

            string retValue = SendToServer(request, objName, port, host);
            var retObj = MarshalFunction.Unmarshal(retValue, InterpreterInstance);
            return retObj;
        }

        public string SendToServer(string request, string load, int port, string host = "localhost")
        {
            // Data buffer for incoming data.
            byte[] bytes = new byte[1024];
            Utils.CheckNotEmpty(request, "request");
            Utils.CheckNotEmpty(load, "load");
            var objLoad = MarshalFunction.Marshal(load, InterpreterInstance);

            if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost"))
            {
                host = Dns.GetHostName();
            }

            string retValue = "";
            try
            {
                IPHostEntry ipHostInfo = Dns.GetHostEntry(host);
                IPAddress ipAddress = ipHostInfo.AddressList[0];
                IPEndPoint remoteEP = new IPEndPoint(ipAddress, port);

                // Create a TCP/IP  socket.
                Socket sender = new Socket(AddressFamily.InterNetwork,
                        SocketType.Stream, ProtocolType.Tcp);

                sender.Connect(remoteEP);

                InterpreterInstance.AppendOutput("Connected to [" + sender.RemoteEndPoint.ToString() + "]", true);

                ServerSocket.SendMessage(sender, request);
                ServerSocket.ReceiveMessage(sender);
                ServerSocket.SendMessage(sender, objLoad);
                retValue = ServerSocket.ReceiveMessage(sender);

                sender.Shutdown(SocketShutdown.Both);
                sender.Close();

            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't connect to server: (" + exc.Message + ")", exc);
            }

            return retValue;
        }
    }

    // Returns current directory name
    class PwdFunction : ParserFunction, IStringFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string path = Directory.GetCurrentDirectory();
            return new Variable(path);
        }
    }

    // Equivalent to cd.. on Windows: one directory up
    class Cd__Function : ParserFunction, IStringFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string newDir = null;

            try
            {
                string pwd = Directory.GetCurrentDirectory();
                DirectoryInfo parent = Directory.GetParent(pwd);
                if (parent == null)
                {
                    throw new ArgumentException("No parent exists.");
                }
                newDir = parent.FullName;
                Directory.SetCurrentDirectory(newDir);
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't change directory: " + exc.Message, exc);
            }

            return new Variable(newDir);
        }
    }

    // Changes directory to the passed one
    class CdFunction : ParserFunction, IStringFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            if (script.Substr().StartsWith(" .."))
            {
                script.Forward();
            }
            string newDir = Utils.GetItem(script).AsString();

            try
            {
                if (newDir == "..")
                {
                    string pwd = Directory.GetCurrentDirectory();
                    DirectoryInfo parent = Directory.GetParent(pwd);
                    if (parent == null)
                    {
                        throw new ArgumentException("No parent exists.");
                    }
                    newDir = parent.FullName;
                }
                if (newDir.Length == 0)
                {
                    newDir = Environment.GetEnvironmentVariable("HOME");
                }
                Directory.SetCurrentDirectory(newDir);

                newDir = Directory.GetCurrentDirectory();
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't change directory: " + exc.Message, exc);
            }

            return new Variable(newDir);
        }
    }

    // Reads a file and returns all lines of that file as a "tuple" (list)
    class ReadCSCSFileFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            string[] lines = Utils.GetFileLines(filename);

            List<Variable> results = Utils.ConvertToResults(lines);

            return new Variable(results);
        }
    }
    
    class ReadCSCSFileAllTextFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            string text = Utils.GetFileContents(filename);

            Variable result = new Variable(text);

            return new Variable(result);
        }
    }


    // View the contents of a text file
    class MoreFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            int size = Constants.DEFAULT_FILE_LINES;

            bool sizeAvailable = Utils.SeparatorExists(script);
            if (sizeAvailable)
            {
                Variable length = Utils.GetItem(script);
                Utils.CheckPosInt(length, script);
                size = (int)length.Value;
            }

            string[] lines = Utils.GetFileLines(filename, 0, size);
            List<Variable> results = Utils.ConvertToResults(lines);

            return new Variable(results);
        }
    }

    // View the last Constants.DEFAULT_FILE_LINES lines of a file
    class TailFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            int size = Constants.DEFAULT_FILE_LINES;

            bool sizeAvailable = Utils.SeparatorExists(script);
            if (sizeAvailable)
            {
                Variable length = Utils.GetItem(script);
                Utils.CheckPosInt(length, script);
                size = (int)length.Value;
            }

            string[] lines = Utils.GetFileLines(filename, -1, size);
            List<Variable> results = Utils.ConvertToResults(lines);

            return new Variable(results);
        }
    }

    // Append a line to a file
    class AppendLineFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            Variable line = Utils.GetItem(script);
            Utils.AppendFileText(filename, line.AsString() + Environment.NewLine);

            return Variable.EmptyInstance;
        }
    }

    // Apend a list of lines to a file
    class AppendLinesFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            string lines = Utils.GetLinesFromList(script);
            Utils.AppendFileText(filename, lines);

            return Variable.EmptyInstance;
        }
    }

    // Write a line to a file
    class WriteLineFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string filename = Utils.GetItem(script).AsString();
            Variable line = Utils.GetItem(script);
            Utils.WriteFileText(filename, line.AsString() + Environment.NewLine);

            return Variable.EmptyInstance;
        }
    }

    // Write a list of lines to a file
    class WriteLinesFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            //string filename = Utils.ResultToString(Utils.GetItem(script));
            string filename = Utils.GetItem(script).AsString();
            string lines = Utils.GetLinesFromList(script);
            Utils.WriteFileText(filename, lines);

            return Variable.EmptyInstance;
        }
    }

    // Find a string in files
    class FindstrFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string search = Utils.GetItem(script).AsString();
            List<string> patterns = Utils.GetFunctionArgs(script);

            bool ignoreCase = true;
            if (patterns.Count > 0 && patterns.Last().Equals("case"))
            {
                ignoreCase = false;
                patterns.RemoveAt(patterns.Count - 1);
            }
            if (patterns.Count == 0)
            {
                patterns.Add("*.*");
            }

            List<Variable> results = null;
            try
            {
                string pwd = Directory.GetCurrentDirectory();
                List<string> files = Utils.GetStringInFiles(pwd, search, patterns.ToArray(), ignoreCase);

                results = Utils.ConvertToResults(files.ToArray(), InterpreterInstance);
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't find pattern: " + exc.Message, exc);
            }

            return new Variable(results);
        }
    }

    // Find files having a given pattern
    class FindfilesFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            List<string> patterns = Utils.GetFunctionArgs(script);
            if (patterns.Count == 0)
            {
                patterns.Add("*.*");
            }

            List<Variable> results = null;
            try
            {
                string pwd = Directory.GetCurrentDirectory();
                List<string> files = Utils.GetFiles(pwd, patterns.ToArray());

                results = Utils.ConvertToResults(files.ToArray(), InterpreterInstance);
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't list directory: " + exc.Message, exc);
            }

            return new Variable(results);
        }
    }

    // Copy a file or a directiry
    class CopyFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string source = Utils.GetItem(script).AsString();
            script.MoveForwardIf(Constants.NEXT_ARG, Constants.SPACE);
            string dest = Utils.GetItem(script).AsString();

            string src = Path.GetFullPath(source);
            string dst = Path.GetFullPath(dest);

            List<Variable> srcPaths = Utils.GetPathnames(src);
            bool multipleFiles = srcPaths.Count > 1;
            if (dst.EndsWith("*"))
            {
                dst = dst.Remove(dst.Count() - 1);
            }
            if ((multipleFiles || Directory.Exists(src)) &&
                !Directory.Exists(dst))
            {
                try
                {
                    Directory.CreateDirectory(dst);
                }
                catch (Exception exc)
                {
                    throw new ArgumentException("Couldn't create [" + dst + "] :" + exc.Message, exc);
                }

            }

            foreach (Variable srcPath in srcPaths)
            {
                string filename = Path.GetFileName(srcPath.String);
                //string dstPath  = Path.Combine(dst, filename);
                Utils.Copy(srcPath.String, dst);
            }

            return Variable.EmptyInstance;
        }
    }

    // Move a file or a directiry
    class MoveFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string source = Utils.GetItem(script).AsString();
            script.MoveForwardIf(Constants.NEXT_ARG, Constants.SPACE);
            string dest = Utils.GetItem(script).AsString();

            string src = Path.GetFullPath(source);
            string dst = Path.GetFullPath(dest);

            bool isFile = File.Exists(src);
            bool isDir = Directory.Exists(src);
            if (!isFile && !isDir)
            {
                throw new ArgumentException("[" + src + "] doesn't exist");
            }

            if (isFile && Directory.Exists(dst))
            {
                // If filename is missing in the destination file,
                // add it from the source.
                dst = Path.Combine(dst, Path.GetFileName(src));
            }

            try
            {
                if (isFile)
                {
                    File.Move(src, dst);
                }
                else
                {
                    Directory.Move(src, dst);
                }
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't copy: " + exc.Message, exc);
            }

            return Variable.EmptyInstance;
        }
    }

    // Make a directory
    class MkdirFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string dirname = Utils.GetItem(script).AsString();
            try
            {
                Directory.CreateDirectory(dirname);
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't create [" + dirname + "] :" + exc.Message, exc);
            }

            return Variable.EmptyInstance;
        }
    }

    // Delete a file or a directory
    class DeleteFunction : ParserFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string pathname = Utils.GetItem(script).AsString();

            bool isFile = File.Exists(pathname);
            bool isDir = Directory.Exists(pathname);
            if (!isFile && !isDir)
            {
                throw new ArgumentException("[" + pathname + "] doesn't exist");
            }
            try
            {
                if (isFile)
                {
                    File.Delete(pathname);
                }
                else
                {
                    Directory.Delete(pathname, true);
                }
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't delete [" + pathname + "] :" + exc.Message, exc);
            }

            return Variable.EmptyInstance;
        }
    }

    // Checks if a directory or a file exists
    class ExistsFunction : ParserFunction, INumericFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string pathname = Utils.GetItem(script).AsString();

            bool exists = false;
            try
            {
                exists = File.Exists(pathname);
                if (!exists)
                {
                    exists = Directory.Exists(pathname);
                }
            }
            catch (Exception)
            {
            }

            return new Variable(exists);
        }
    }

    // List files in a directory
    class DirFunction : ParserFunction, IArrayFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            string dirname = (!script.StillValid() || script.Current == Constants.END_STATEMENT) ?
              Directory.GetCurrentDirectory() :
              Utils.GetToken(script, Constants.NEXT_OR_END_ARRAY);

            //List<Variable> results = Utils.GetPathnames(dirname);
            List<Variable> results = new List<Variable>();

            int index = dirname.IndexOf('*');
            if (index < 0 && !Directory.Exists(dirname) && !File.Exists(dirname))
            {
                throw new ArgumentException("Directory [" + dirname + "] doesn't exist");
            }

            string pattern = Constants.ALL_FILES;

            try
            {
                string dir = index < 0 ? Path.GetFullPath(dirname) : dirname;
                if (File.Exists(dir))
                {
                    FileInfo fi = new FileInfo(dir);
                    InterpreterInstance.AppendOutput(Utils.GetPathDetails(fi, fi.Name), true);
                    results.Add(new Variable(fi.Name));
                    return new Variable(results);
                }
                // Special dealing if there is a pattern (only * is supported at the moment)
                if (index >= 0)
                {
                    pattern = Path.GetFileName(dirname);

                    if (index > 0)
                    {
                        string prefix = dirname.Substring(0, index);
                        DirectoryInfo di = Directory.GetParent(prefix);
                        dirname = di.FullName;
                    }
                    else
                    {
                        dirname = ".";
                    }
                }
                dir = Path.GetFullPath(dirname);
                // First get contents of the directory (unless there is a pattern)
                DirectoryInfo dirInfo = new DirectoryInfo(dir);

                if (pattern == Constants.ALL_FILES)
                {
                    InterpreterInstance.AppendOutput(Utils.GetPathDetails(dirInfo, "."), true);
                    if (dirInfo.Parent != null)
                    {
                        InterpreterInstance.AppendOutput(Utils.GetPathDetails(dirInfo.Parent, ".."), true);
                    }
                }

                // Then get contents of all of the files in the directory
                FileInfo[] fileNames = dirInfo.GetFiles(pattern);
                foreach (FileInfo fi in fileNames)
                {
                    try
                    {
                        InterpreterInstance.AppendOutput(Utils.GetPathDetails(fi, fi.Name), true);
                        results.Add(new Variable(fi.Name));
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                // Then get contents of all of the subdirs in the directory
                DirectoryInfo[] dirInfos = dirInfo.GetDirectories(pattern);
                foreach (DirectoryInfo di in dirInfos)
                {
                    try
                    {
                        InterpreterInstance.AppendOutput(Utils.GetPathDetails(di, di.Name), true);
                        results.Add(new Variable(di.Name));
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
            }
            catch (Exception exc)
            {
                throw new ArgumentException("Couldn't list directory: " + exc.Message, exc);
            }

            return new Variable(results);
        }
    }

    class TimestampFunction : ParserFunction, IStringFunction
    {
        bool m_millis = false;
        public TimestampFunction(bool millis = false)
        {
            m_millis = millis;
        }
        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();

            double timestamp = Utils.GetSafeDouble(args, 0);
            string strFormat = Utils.GetSafeString(args, 1, "yyyy/MM/dd HH:mm:ss.fff");
            Utils.CheckNotEmpty(strFormat, m_name);

            var dt = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            if (m_millis)
            {
                dt = dt.AddMilliseconds(timestamp);
            }
            else
            {
                dt = dt.AddSeconds(timestamp);
            }

            DateTime runtimeKnowsThisIsUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            DateTime localVersion = runtimeKnowsThisIsUtc.ToLocalTime();
            string when = localVersion.ToString(strFormat);
            return new Variable(when);
        }
    }

    class StopWatchFunction : ParserFunction, IStringFunction
    {
        static System.Diagnostics.Stopwatch m_stopwatch = new System.Diagnostics.Stopwatch();
        public enum Mode { START, STOP, ELAPSED, TOTAL_SECS, TOTAL_MS };

        Mode m_mode;
        public StopWatchFunction(Mode mode)
        {
            m_mode = mode;
        }

        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();

            if (m_mode == Mode.START)
            {
                m_stopwatch.Restart();
                return Variable.EmptyInstance;
            }

            string strFormat = Utils.GetSafeString(args, 0, "secs");
            string elapsedStr = "";
            double elapsed = -1.0;
            if (strFormat == "hh::mm:ss.fff")
            {
                elapsedStr = string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}",
                  m_stopwatch.Elapsed.Hours, m_stopwatch.Elapsed.Minutes,
                  m_stopwatch.Elapsed.Seconds, m_stopwatch.Elapsed.Milliseconds);
            }
            else if (strFormat == "mm:ss.fff")
            {
                elapsedStr = string.Format("{0:D2}:{1:D2}.{2:D3}",
                    m_stopwatch.Elapsed.Minutes,
                    m_stopwatch.Elapsed.Seconds, m_stopwatch.Elapsed.Milliseconds);
            }
            else if (strFormat == "mm:ss")
            {
                elapsedStr = string.Format("{0:D2}:{1:D2}",
                    m_stopwatch.Elapsed.Minutes,
                    m_stopwatch.Elapsed.Seconds);
            }
            else if (strFormat == "ss.fff")
            {
                elapsedStr = string.Format("{0:D2}.{1:D3}",
                    m_stopwatch.Elapsed.Seconds, m_stopwatch.Elapsed.Milliseconds);
            }
            else if (strFormat == "secs")
            {
                elapsed = Math.Round(m_stopwatch.Elapsed.TotalSeconds);
            }
            else if (strFormat == "ms")
            {
                elapsed = Math.Round(m_stopwatch.Elapsed.TotalMilliseconds);
            }

            if (m_mode == Mode.STOP)
            {
                m_stopwatch.Stop();
            }

            return elapsed >= 0 ? new Variable(elapsed) : new Variable(elapsedStr);
        }
    }

    class LabelFunction : ActionFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            // Just skip this label. m_name is equal to the lable name.
            return Variable.EmptyInstance;
        }
    }

    class PointerFunction : ActionFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            List<string> args = Utils.GetTokens(script);
            Utils.CheckArgs(args.Count, 1, m_name);

            var result = new Variable(Variable.VarType.POINTER);
            result.Pointer = args[0];
            InterpreterInstance.AddGlobalOrLocalVariable(m_name,
                                        new GetVarFunction(result), script);
            return result;
        }
    }

    class PointerReferenceFunction : ActionFunction
    {
        protected override Variable Evaluate(ParsingScript script)
        {
            var pointer = Utils.GetToken(script, Constants.TOKEN_SEPARATION);

            var result = GetRefValue(pointer, script);
            return result;
        }

        public Variable GetRefValue(string pointer, ParsingScript script)
        {
            if (string.IsNullOrWhiteSpace(pointer))
            {
                return Variable.Undefined;
            }
            var refPointer = InterpreterInstance.GetVariable(pointer, null, true) as GetVarFunction;
            if (refPointer == null || string.IsNullOrWhiteSpace(refPointer.Value.Pointer))
            {
                return Variable.Undefined;
            }

            var result = InterpreterInstance.GetVariable(refPointer.Value.Pointer, null, true);
            if (result is GetVarFunction)
            {
                return ((GetVarFunction)result).Value;
            }

            if (result is CustomFunction)
            {
                script.Forward();
                List<Variable> args = script.GetFunctionArgs();
                return ((CustomFunction)result).Run(args, script);
            }
            return Variable.Undefined;
        }
    }

    class GotoGosubFunction : ParserFunction
    {
        bool m_isGoto = true;

        public GotoGosubFunction(bool gotoMode = true)
        {
            m_isGoto = gotoMode;
        }

        protected override Variable Evaluate(ParsingScript script)
        {
            var labelName = Utils.GetToken(script, Constants.TOKEN_SEPARATION);

            Dictionary<string, int> labels;
            if (script.AllLabels == null || script.LabelToFile == null |
               !script.AllLabels.TryGetValue(script.FunctionName, out labels))
            {
                Utils.ThrowErrorMsg("Couldn't find labels in function [" + script.FunctionName + "].",
                                    script, m_name);
                return Variable.EmptyInstance;
            }

            int gotoPointer;
            if (!labels.TryGetValue(labelName, out gotoPointer))
            {
                Utils.ThrowErrorMsg("Couldn't find label [" + labelName + "].",
                                    script, m_name);
                return Variable.EmptyInstance;
            }

            string filename;
            if (script.LabelToFile.TryGetValue(labelName, out filename) &&
                filename != script.Filename && !string.IsNullOrWhiteSpace(filename))
            {
                var newScript = script.GetIncludeFileScript(filename);
                script.Filename = filename;
                script.String = newScript.String;
            }

            if (!m_isGoto)
            {
                script.PointersBack.Add(script.Pointer);
            }

            script.Pointer = gotoPointer;
            if (string.IsNullOrWhiteSpace(script.FunctionName))
            {
                script.Backward();
            }

            return Variable.EmptyInstance;
        }
    }

    class EncodeFileFunction : ParserFunction
    {
        bool m_encode = true;

        public EncodeFileFunction(bool encode = true)
        {
            m_encode = encode;
        }

        public static string EncodeText(string plainText)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            var intermidiate = System.Convert.ToBase64String(plainTextBytes);


            //return intermidiate;
            return plainText;
        }

        public static string DecodeText(string base64EncodedData)
        {
            var base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
            var intermidiate = System.Text.Encoding.UTF8.GetString(base64EncodedBytes);


            return intermidiate;
        }

        protected override Variable Evaluate(ParsingScript script)
        {
            List<Variable> args = script.GetFunctionArgs();
            Utils.CheckArgs(args.Count, 1, m_name, true);
            string filename = args[0].AsString();
            string pathname = script.GetFilePath(filename);

            return EncodeDecode(pathname, m_encode);
        }

        public static Variable EncodeDecode(string pathname, bool encode)
        {
            string text = Utils.GetFileText(pathname);
            string newText = "";

            try
            {
                newText = encode ? EncodeText(text) : DecodeText(text);
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc.Message);
                return Variable.EmptyInstance;
            }

            Utils.WriteFileText(pathname, newText);
            return new Variable(pathname);
        }
    }
}
