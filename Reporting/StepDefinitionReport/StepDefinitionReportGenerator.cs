using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using System.Xml.Xsl;
using TechTalk.SpecFlow.Generator.Configuration;
using TechTalk.SpecFlow.Parser.SyntaxElements;
using TechTalk.SpecFlow.Reporting.StepDefinitionReport.ReportElements;

namespace TechTalk.SpecFlow.Reporting.StepDefinitionReport
{
    public class StepDefinitionReportGenerator
    {
        private readonly SpecFlowProject specFlowProject;
        private readonly List<BindingInfo> bindings;
        private readonly List<Feature> parsedFeatures;
        private readonly bool showBindingsWithoutInsance;

        private ReportElements.StepDefinitionReport report;
        private Dictionary<BindingInfo, StepDefinition> stepDefByBinding;
        private Dictionary<StepDefinition, BindingInfo> bindingByStepDef;
        private readonly List<StepDefinition> stepDefsWithNoBinding = new List<StepDefinition>();

        public StepDefinitionReportGenerator(string projectFile, string binFolder, bool showBindingsWithoutInsance)
        {
            specFlowProject = MsBuildProjectReader.LoadSpecFlowProjectFromMsBuild(projectFile);

            parsedFeatures = ParserHelper.GetParsedFeatures(specFlowProject);

            var basePath = Path.Combine(specFlowProject.ProjectFolder, binFolder);
            bindings = BindingCollector.CollectBindings(specFlowProject, basePath);

            this.showBindingsWithoutInsance = showBindingsWithoutInsance;
        }

        public StepDefinitionReportGenerator(SpecFlowProject specFlowProject, List<BindingInfo> bindings, List<Feature> parsedFeatures, bool showBindingsWithoutInsance)
        {
            this.specFlowProject = specFlowProject;
            this.showBindingsWithoutInsance = showBindingsWithoutInsance;
            this.bindings = bindings;
            this.parsedFeatures = parsedFeatures;
        }

        public ReportElements.StepDefinitionReport GenerateReport()
        {
            report = new ReportElements.StepDefinitionReport();
            report.ProjectName = specFlowProject.ProjectName;
            report.GeneratedAt = DateTime.Now.ToString("g", CultureInfo.InvariantCulture);
            report.ShowBindingsWithoutInsance = showBindingsWithoutInsance;

            stepDefByBinding = new Dictionary<BindingInfo, StepDefinition>();
            bindingByStepDef = new Dictionary<StepDefinition, BindingInfo>();

            foreach (var bindingInfo in bindings)
            {
                var stepDefinition = new StepDefinition();
                stepDefinition.Binding = new Binding { MethodReference = bindingInfo.MethodReference };
                stepDefinition.Type = bindingInfo.BindingType;

                stepDefByBinding.Add(bindingInfo, stepDefinition);
                bindingByStepDef.Add(stepDefinition, bindingInfo);
                report.StepDefinitions.Add(stepDefinition);
            }

            foreach (var feature in parsedFeatures)
            {
                var featureRef = new FeatureRef { FilePath = feature.SourceFile, Name = feature.Title };
                if (feature.Background != null)
                {
                    var scenarioRef = new ScenarioRef { Name = "Background" };
                    AddStepInstances(featureRef, scenarioRef, feature.Background.Steps, false);
                }

                foreach (var scenario in feature.Scenarios)
                {
                    var scenarioRef = new ScenarioRef { Name = scenario.Title, SourceFileLine = scenario.FilePosition == null ? -1 : scenario.FilePosition.Line };
                    if (scenario is ScenarioOutline)
                    {
                        ScenarioSteps firstExampleSteps = CreateFirstExampleScenarioSteps((ScenarioOutline) scenario);
                        AddStepInstances(featureRef, scenarioRef, firstExampleSteps, true);
                    }
                    else
                    {
                        AddStepInstances(featureRef, scenarioRef, scenario.Steps, false);
                    }
                }
            }

            foreach (var stepDefinition in report.StepDefinitions)
            {
                if (stepDefinition.ScenarioStep == null)
                {
                    CreateSampleStep(stepDefinition);
                }
                
                if (stepDefinition.Instances.Count == 0)
                {
                    stepDefinition.Instances = null;
                }
            }

            return report;
        }

        private ScenarioSteps CreateFirstExampleScenarioSteps(ScenarioOutline scenarioOutline)
        {
            foreach (var exampleSet in scenarioOutline.Examples.ExampleSets)
            {
                foreach (var example in exampleSet.Table.Body)
                {
                    Dictionary<string, string> paramSubst = new Dictionary<string, string>();
                    for (int i = 0; i < exampleSet.Table.Header.Cells.Length; i++)
                    {
                        paramSubst.Add(exampleSet.Table.Header.Cells[i].Value, example.Cells[i].Value);
                    }

                    return CreateScenarioSteps(scenarioOutline, paramSubst);
                }
            }
            return new ScenarioSteps();
        }

        private ScenarioSteps CreateScenarioSteps(ScenarioOutline scenarioOutline, Dictionary<string, string> paramSubst)
        {
            ScenarioSteps result = new ScenarioSteps();
            foreach (var scenarioStep in scenarioOutline.Steps)
            {
                var newStep = Clone(scenarioStep);
                newStep.Text = GetReplacedText(newStep.Text, paramSubst);
                newStep.MultiLineTextArgument = GetReplacedText(newStep.MultiLineTextArgument, paramSubst);

                if (newStep.TableArg != null)
                {
                    foreach (var row in newStep.TableArg.Body)
                    {
                        foreach (var cell in row.Cells)
                        {
                            cell.Value = GetReplacedText(cell.Value, paramSubst);
                        }
                    }
                }

                result.Add(newStep);
            }
            return result;
        }

        private string GetReplacedText(string text, Dictionary<string, string> paramSubst)
        {
            if (text == null || paramSubst == null)
                return text;

            foreach (var subst in paramSubst)
            {
                text = text.Replace("<" + subst.Key + ">", subst.Value);
            }

            return text;
        }

        private void CreateSampleStep(StepDefinition stepDefinition)
        {
            BindingInfo bindingInfo = bindingByStepDef[stepDefinition];
            Debug.Assert(bindingInfo != null);
            switch (stepDefinition.Type)
            {
                case "Given":
                    stepDefinition.ScenarioStep = new Given();
                    break;
                case "When":
                    stepDefinition.ScenarioStep = new When();
                    break;
                case "Then":
                    stepDefinition.ScenarioStep = new Then();
                    break;
                default:
                    throw new InvalidOperationException();
            }

            stepDefinition.ScenarioStep.Text = GetSampleText(bindingInfo);
        }

        private string GetSampleText(BindingInfo bindingInfo, string text, Match match)
        {
            StringBuilder sampleText = new StringBuilder(text);
            for (int groupIndex = match.Groups.Count - 1; groupIndex >= 1; groupIndex--)
            {
                int paramIndex = groupIndex - 1;
                var paramText = paramIndex >= bindingInfo.ParameterNames.Length ? "{?param?}" :
                                                                                                  "{" + bindingInfo.ParameterNames[paramIndex] + "}";

                var gr = match.Groups[groupIndex];
                sampleText.Remove(gr.Index, gr.Length);
                sampleText.Insert(gr.Index, paramText);
            }
            return sampleText.ToString();
        }

        private string GetSampleText(BindingInfo bindingInfo)
        {
            var sampleText = bindingInfo.Regex.ToString().Trim('$', '^');
            Regex re = new Regex(@"\([^\)]+\)");
            int paramIndex = 0;
            sampleText = re.Replace(sampleText, delegate
                                                    {
                                                        return paramIndex >= bindingInfo.ParameterNames.Length ? "{?param?}" :
                                                                                                                                 "{" + bindingInfo.ParameterNames[paramIndex++] + "}";
                                                    });
            return sampleText;
        }

        public void TransformReport(string outputFilePath)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(ReportElements.StepDefinitionReport), ReportElements.StepDefinitionReport.XmlNamespace);

            if (XsltHelper.IsXmlOutput(outputFilePath))
            {
                XsltHelper.TransformXml(serializer, report, outputFilePath);
            }
            else
            {
                XsltHelper.TransformHtml(serializer, report, GetType(), outputFilePath, specFlowProject.GeneratorConfiguration);
            }
        }

        private void AddStepInstances(FeatureRef featureRef, ScenarioRef scenarioRef, IEnumerable<ScenarioStep> scenarioSteps, bool fromScenarioOutline)
        {
            string currentBlock = "";

            foreach (var scenarioStep in scenarioSteps)
            {
                if (scenarioStep is Given || scenarioStep is When || scenarioStep is Then)
                    currentBlock = scenarioStep.GetType().Name;

                bool found = false;

                foreach (var bindingInfo in bindings)
                {
                    if (bindingInfo.BindingType != currentBlock)
                        continue;

                    var match = bindingInfo.Regex.Match(scenarioStep.Text);
                    if (!match.Success)
                        continue;

                    Instance instance = new Instance
                                            {
                                                FromScenarioOutline = fromScenarioOutline,
                                                ScenarioStep = scenarioStep,
                                                FeatureRef = featureRef,
                                                ScenarioRef = scenarioRef
                                            };
                    var regexArgs = match.Groups.Cast<Group>().Skip(1).Select(g => g.Value).ToArray();
                    if (regexArgs.Length > 0)
                    {
                        instance.Parameters = new List<Parameter>();
                        for (int i = 0; i < Math.Min(regexArgs.Length, bindingInfo.ParameterNames.Length); i++)
                            instance.Parameters.Add(new Parameter { Name = bindingInfo.ParameterNames[i], Value = regexArgs[i] });
                    }

                    var stepDefinition = stepDefByBinding[bindingInfo];
                    stepDefinition.Instances.Add(instance);

                    if (stepDefinition.ScenarioStep == null)
                    {
                        stepDefinition.ScenarioStep = CloneTo(scenarioStep, currentBlock);
                        stepDefinition.ScenarioStep.Text = GetSampleText(bindingInfo, stepDefinition.ScenarioStep.Text, match);
                    }

                    found = true;
                    break;
                }

                if (!found)
                {
                    var stepDefinition =
                        stepDefsWithNoBinding.FirstOrDefault(sd => sd.ScenarioStep.Text.Equals(scenarioStep.Text));

                    if (stepDefinition == null)
                    {
                        stepDefinition = new StepDefinition();
                        stepDefinition.Type = currentBlock;
                        stepDefinition.ScenarioStep = CloneTo(scenarioStep, currentBlock);
                        stepDefsWithNoBinding.Add(stepDefinition);
                        report.StepDefinitions.Add(stepDefinition);
                    }

                    Instance instance = new Instance
                                            {
                                                FromScenarioOutline = fromScenarioOutline,
                                                ScenarioStep = scenarioStep,
                                                FeatureRef = featureRef,
                                                ScenarioRef = scenarioRef
                                            };
                    stepDefinition.Instances.Add(instance);
                }
            }
        }

        private ScenarioStep Clone(ScenarioStep step)
        {
            ScenarioStep newStep = null;
            if (step is Given)
                newStep = new Given();
            else if (step is When)
                newStep = new When();
            else if (step is Then)
                newStep = new Then();
            else if (step is And)
                newStep = new And();
            else if (step is But)
                newStep = new But();

            Debug.Assert(newStep != null);

            newStep.Text = step.Text;
            newStep.MultiLineTextArgument = step.MultiLineTextArgument;
            newStep.TableArg = Clone(step.TableArg);
            return newStep;
        }

        private Table Clone(Table table)
        {
            if (table == null)
                return null;

            return new Table(Clone(table.Header), table.Body.Select(r => Clone(r)).ToArray());
        }

        private Row Clone(Row row)
        {
            return new Row(row.Cells.Select(c => new Cell(){Value = c.Value}).ToArray());
        }

        private ScenarioStep CloneTo(ScenarioStep step, string currentBlock)
        {
            ScenarioStep newStep = null;
            if (currentBlock == "Given")
                newStep = new Given();
            else if (currentBlock == "When")
                newStep = new When();
            else if (currentBlock == "Then")
                newStep = new Then();

            Debug.Assert(newStep != null);

            newStep.Text = step.Text;
            newStep.MultiLineTextArgument = step.MultiLineTextArgument;
            newStep.TableArg = Clone(step.TableArg);
            return newStep;
        }
    }
}