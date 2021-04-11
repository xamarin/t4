// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.Build.Utilities;
using Microsoft.VisualStudio.TextTemplating;

namespace Mono.TextTemplating.Build
{
	class TextTransformProcessor
	{
		TaskLoggingHelper Log { get; }

		public TextTransformProcessor (TaskLoggingHelper log)
		{
			Log = log;
		}

		public bool Process (TemplateBuildState previousSession, TemplateBuildState session, bool preprocessOnly)
		{
			(var transforms, var preprocessed) = session.GetStaleAndNewTemplates (previousSession, preprocessOnly, new WriteTimeCache ().GetWriteTime);

			if ((transforms == null || transforms.Count == 0) && (preprocessed == null || preprocessed.Count == 0)) {
				return true;
			}

			var generator = new MSBuildTemplateGenerator ();
			if (session.ReferencePaths != null) {
				generator.ReferencePaths.AddRange (session.ReferencePaths);
			}
			if (session.AssemblyReferences != null) {
				generator.Refs.AddRange (session.AssemblyReferences);
			}
			if (session.IncludePaths != null) {
				generator.IncludePaths.AddRange (session.IncludePaths);
			}
			if (session.DirectiveProcessors != null) {
				foreach (var dp in session.DirectiveProcessors) {
					generator.AddDirectiveProcessor(dp.Name, dp.Class, dp.Assembly);
				}
			}
			if (session.Parameters != null) {
				foreach (var par in session.Parameters) {
					generator.AddParameter (par.Processor, par.Directive, par.Name, par.Value);
				}
			}

			bool success = true;

			var processedOutput = new List<string> ();

			if (transforms != null) {
				if (!success) {
					return false;
				}

				var parameterMap = session.Parameters?.ToDictionary (p => p.Name, p => p.Value);

				foreach (var transform in transforms) {
					string inputFile = transform.InputFile;
					string outputFile = Path.ChangeExtension (inputFile, ".txt");
					var pt = LoadTemplate (inputFile, out var inputContent);
					TemplateSettings settings = TemplatingEngine.GetSettings (generator, pt);

					if (parameterMap != null) {
						AddCoercedSessionParameters (generator, pt, parameterMap);
					}

					if (LogAndClear (pt.Errors, transform.InputFile)) {
						success = false;
						continue;
					}

					var outputContent = generator.ProcessTemplate (pt, inputFile, inputContent, ref outputFile, settings);

					if (LogAndClear (generator.Errors, inputFile)) {
						success = false;
						continue;
					}

					transform.OutputFile = outputFile;

					WriteOutput (outputFile, outputContent, settings.Encoding);
				}
			}

			var preprocessedOutput = new List<string> ();

			if (preprocessed != null) {
				foreach (var preprocess in preprocessed) {
					string inputFile = preprocess.InputFile;

					var pt = LoadTemplate (inputFile, out var inputContent);
					TemplateSettings settings = TemplatingEngine.GetSettings (generator, pt);
					if (settings.Namespace == null) {
						settings.Namespace = session.DefaultNamespace;
					}

					if (LogAndClear (pt.Errors, preprocess.InputFile)) {
						success = false;
						continue;
					}

					//FIXME: escaping
					//FIXME: namespace name based on relative path and link metadata
					string preprocessClassName = Path.GetFileNameWithoutExtension (inputFile);

					var outputContent = generator.PreprocessTemplate (pt, inputFile, inputContent, preprocessClassName, settings);

					if (LogAndClear (generator.Errors, inputFile)) {
						success = false;
						continue;
					}

					WriteOutput (preprocess.OutputFile, outputContent, settings.Encoding);
				}
			}

			return success;

			ParsedTemplate LoadTemplate (string filename, out string inputContent)
			{
				if (!File.Exists (filename)) {
					Log.LogError ("Template file '{0}' does not exist", filename);
					success = false;
					inputContent = null;
					return null;
				}

				try {
					inputContent = File.ReadAllText (filename);
				}
				catch (IOException ex) {
					Log.LogErrorFromException (ex, true, true, filename);
					success = false;
					inputContent = null;
					return null;
				}

				return ParsedTemplate.FromText (inputContent, generator);
			}

			void WriteOutput (string outputFile, string outputContent, Encoding encoding)
			{
				try {
					File.WriteAllText (outputFile, outputContent, encoding ?? new UTF8Encoding (encoderShouldEmitUTF8Identifier: false));
				}
				catch (IOException ex) {
					Log.LogErrorFromException (ex, true, true, outputFile);
					success = false;
				}
			}
		}

		class WriteTimeCache
		{
			public DateTime GetWriteTime (string filepath)
			{
				if (!writeTimeCache.TryGetValue (filepath, out var value)) {
					writeTimeCache.Add (filepath, value = File.GetLastWriteTime (filepath));
				}
				return value;
			}
			readonly Dictionary<string, DateTime> writeTimeCache = new ();
		}


		bool LogAndClear (CompilerErrorCollection errors, string file)
		{
			bool hasErrors = false;

			foreach (CompilerError err in errors) {
				if (err.IsWarning) {
					Log.LogWarning (null, err.ErrorNumber, null, err.FileName ?? file, err.Line, err.Column, 0, 0, err.ErrorText);
				} else {
					hasErrors = true;
					Log.LogError (null, err.ErrorNumber, null, err.FileName ?? file, err.Line, err.Column, 0, 0, err.ErrorText);
				}
			}

			errors.Clear ();

			return hasErrors;
		}

		static void AddCoercedSessionParameters (MSBuildTemplateGenerator generator, ParsedTemplate pt, Dictionary<string, string> properties)
		{
			if (properties.Count == 0) {
				return;
			}

			var session = generator.GetOrCreateSession ();

			foreach (var p in properties) {
				var directive = pt.Directives.FirstOrDefault (d =>
					d.Name == "parameter" &&
					d.Attributes.TryGetValue ("name", out string attVal) &&
					attVal == p.Key);

				if (directive != null) {
					directive.Attributes.TryGetValue ("type", out string typeName);
					var mappedType = ParameterDirectiveProcessor.MapTypeName (typeName);
					if (mappedType != "System.String") {
						if (ConvertType (mappedType, p.Value, out object converted)) {
							session[p.Key] = converted;
							continue;
						}

						pt.Errors.Add (
							new CompilerError (
								null, 0, 0, null,
								$"Could not convert property '{p.Key}'='{p.Value}' to parameter type '{typeName}'"
							)
						);
					}
				}
				session[p.Key] = p.Value;
			}
		}

		static bool ConvertType (string typeName, string value, out object converted)
		{
			converted = null;
			try {
				var type = Type.GetType (typeName);
				if (type == null) {
					return false;
				}
				Type stringType = typeof (string);
				if (type == stringType) {
					return true;
				}
				var converter = System.ComponentModel.TypeDescriptor.GetConverter (type);
				if (converter == null || !converter.CanConvertFrom (stringType)) {
					return false;
				}
				converted = converter.ConvertFromString (value);
				return true;
			}
			catch {
			}
			return false;
		}
	}
}