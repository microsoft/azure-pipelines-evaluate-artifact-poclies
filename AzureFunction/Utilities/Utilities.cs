﻿namespace Microsoft.Azure.Pipelines.EvaluateArtifactPolicies
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;

    using Microsoft.Azure.Pipelines.EvaluateArtifactPolicies.Models;
    using Microsoft.Azure.Pipelines.EvaluateArtifactPolicies.Request;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    using WebJobsExecutionContext = WebJobs.ExecutionContext;

    public static class Utilities
    {
        private const string ImageProvenanceFileName = "ImageProvenance.json";
        private const string PolicyFileName = "Policies.rego";
        private const string OutputResultFileName = "Output.txt";
        private const int PackageRegexMatchTimeout = 1; // 1 second

        private const string DebugVariableName = "system.debug";

        public static IEnumerable<string> ExecutePolicyCheck(
            WebJobsExecutionContext executionContext,
            ILogger log,
            string imageProvenance,
            string policy,
            TaskLogger taskLogger,
            IDictionary<string, string> variables,
            StringBuilder syncLogger,
            out string outputLog)
        {
            string folderName = string.Format("Policy-{0}", executionContext.InvocationId.ToString("N"));
            string newFolderPath = Path.Combine(executionContext.FunctionDirectory, folderName);

            string imageProvenancePath = Path.Combine(newFolderPath, ImageProvenanceFileName);
            string policyFilePath = Path.Combine(newFolderPath, PolicyFileName);

            string packageName = Regex.Match(policy, @"package\s([a-zA-Z0-9.]+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Groups?[1].Value;
            Utilities.LogInformation(string.Format(CultureInfo.InvariantCulture, "Package name : {0}", packageName), log, taskLogger, variables, syncLogger);


            if (string.IsNullOrWhiteSpace(packageName))
            {
                outputLog = "No package name could be inferred from the policy. Cannot continue execution. Ensure that policy contains a package name defined";
                Utilities.LogInformation(outputLog, log, taskLogger, variables, syncLogger, true);

                return new List<string> { outputLog };
            }

            string output;

            try
            {
                Directory.CreateDirectory(newFolderPath);
                log.LogInformation(string.Format("Folder created : {0}", newFolderPath), log, taskLogger, variables, syncLogger);

                File.WriteAllText(imageProvenancePath, imageProvenance);
                string formattedImageProvenance = IsDebugEnabled(variables) ? JValue.Parse(imageProvenance).ToString(Formatting.Indented) : imageProvenance;
                Utilities.LogInformation("Image provenance file created", log, taskLogger, variables, syncLogger);
                Utilities.LogInformation($"Image provenance : \r\n{formattedImageProvenance}", log, taskLogger, variables, syncLogger);
                File.WriteAllText(policyFilePath, policy);
                Utilities.LogInformation("Policy content file created", log, taskLogger, variables, syncLogger);
                Utilities.LogInformation($"Policy definitions : \r\n{policy}", log, taskLogger, variables, syncLogger);

                // Add full explanation in case debug is set to true
                var explainMode = IsDebugEnabled(variables) ? "full" : "notes";

                // 2>&1 ensures standard error is directed to standard output
                string arguments = string.Format(
                            CultureInfo.InvariantCulture,
                            "/c \"\"..\\opa_windows_amd64.exe\" eval -f pretty --explain {5} -i \"{0}\\{1}\" -d \"{0}\\{2}\" \"data.{3}.violations\" > \"{0}\\{4}\" 2>&1\"",
                            folderName,
                            ImageProvenanceFileName,
                            PolicyFileName,
                            packageName,
                            OutputResultFileName,
                            explainMode);

                Utilities.LogInformation($"Command line cmd: {arguments}", log, taskLogger, variables, syncLogger);

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        WorkingDirectory = executionContext.FunctionDirectory
                    }
                };

                Utilities.LogInformation("Initiating evaluation", log, taskLogger, variables, syncLogger, true);
                process.Start();
                Utilities.LogInformation("Evaluation is in progress", log, taskLogger, variables, syncLogger, true);
                process.WaitForExit();
                Utilities.LogInformation("Evaluation complete. Processing result", log, taskLogger, variables, syncLogger, true);
                Utilities.LogInformation($"Completed executing with exit code {process.ExitCode}", log, taskLogger, variables, syncLogger);

                output = File.ReadAllText(string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", newFolderPath, OutputResultFileName));
                log.LogInformation(output);

                if (process.ExitCode != 0)
                {
                    outputLog = output;
                    return new List<string> { $"Policy run had issues: {output}" };
                }
            }
            finally
            {
                Directory.Delete(newFolderPath, true);
            }

            return Utilities.GetViolationsFromResponse(log, taskLogger, output, variables, syncLogger, out outputLog);
        }

        public static IEnumerable<string> GetViolationsFromResponse(
        ILogger log,
        TaskLogger taskLogger,
        string output,
        IDictionary<string, string> variables,
        StringBuilder syncLogger,
        out string outputLog)
        {
            IEnumerable<string> violations;
            int outputValueIndex = output.IndexOf("[\n");
            log.LogInformation($" outputValueIndex of [ <new line> : {outputValueIndex}");

            if (outputValueIndex < 0)
            {
                outputValueIndex = output.IndexOf("[]");
                log.LogInformation($" outputValueIndex of [] : {outputValueIndex}");
            }

            string outputValueString = outputValueIndex >= 0 ? output.Substring(outputValueIndex) : string.Empty;
            Utilities.LogInformation($"Output of policy check : {outputValueString}", log, taskLogger, variables, syncLogger);

            JArray outputArray = JsonConvert.DeserializeObject(outputValueString) as JArray;
            if (outputArray != null)
            {
                violations = outputArray.Values().Select(val => val.ToString(Formatting.Indented)).ToList();
            }
            else
            {
                if (output.IndexOf("undefined") == 0 || output.IndexOf("\nundefined") > 0)
                {
                    violations = new List<string> { "violations is not defined in the policy. Please defined a rule called violations" };
                }
                else
                {
                    violations = new List<string>();
                }
            }

            // Get every line in the log to a new line, for better readability in the logs pane
            // Without this step, each line of the output won't go to a new line number
            outputLog = Regex.Replace(output, @"(?<=\S)\n", "\r\n");
            Utilities.LogInformation(outputLog, log, taskLogger, variables, syncLogger, true);

            return violations;
        }

        public static TaskProperties CreateTaskProperties(EvaluationRequest request)
        {
            var taskPropertiesDictionary = new Dictionary<string, string>();
            taskPropertiesDictionary.Add(TaskProperties.AuthTokenKey, request.AuthToken);
            taskPropertiesDictionary.Add(TaskProperties.HubNameKey, request.HubName);
            taskPropertiesDictionary.Add(TaskProperties.PlanUrlKey, request.HostUrl.ToString());
            taskPropertiesDictionary.Add(TaskProperties.JobIdKey, request.JobId.ToString());
            taskPropertiesDictionary.Add(TaskProperties.PlanIdKey, request.PlanId.ToString());
            taskPropertiesDictionary.Add(TaskProperties.TimelineIdKey, request.TimelineId.ToString());

            return new TaskProperties(taskPropertiesDictionary);
        }

        public static void LogInformation(string message, ILogger log, TaskLogger taskLogger, IDictionary<string, string> variables, StringBuilder syncLogger, bool alwaysLog = false)
        {
            if (alwaysLog
                || IsDebugEnabled(variables))
            {
                taskLogger?.Log(message).ConfigureAwait(false);
                syncLogger?.AppendLine(message);
            }

            log.LogInformation(message);
        }

        private static bool IsDebugEnabled(IDictionary<string, string> variables)
        {
            if (variables == null)
            {
                return false;
            }

            string debugVariableValueString;
            bool debugVariableValue;
            return variables.TryGetValue(DebugVariableName, out debugVariableValueString)
                && bool.TryParse(debugVariableValueString, out debugVariableValue)
                && debugVariableValue;
        }
    }
}
