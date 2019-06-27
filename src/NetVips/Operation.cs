namespace NetVips
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using Internal;

    /// <summary>
    /// Wrap a <see cref="VipsOperation"/> object.
    /// </summary>
    public class Operation : VipsObject
    {
        // private static Logger logger = LogManager.GetCurrentClassLogger();

        /// <inheritdoc cref="VipsObject"/>
        private Operation(IntPtr pointer)
            : base(pointer)
        {
            // logger.Debug($"VipsOperation = {pointer}");
        }

        private static Operation NewFromName(string operationName)
        {
            var vop = VipsOperation.New(operationName);
            if (vop == IntPtr.Zero)
            {
                throw new VipsException($"no such operation {operationName}");
            }

            return new Operation(vop);
        }

        /// <summary>
        /// Set a GObject property. The value is converted to the property type, if possible.
        /// </summary>
        /// <param name="name">The name of the property to set.</param>
        /// <param name="flags">See <see cref="Internal.Enums.VipsArgumentFlags"/>.</param>
        /// <param name="value">The value.</param>
        private void Set(string name, Internal.Enums.VipsArgumentFlags flags, object value)
        {
            // MODIFY args need to be copied before they are set
            if ((flags & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_MODIFY) != 0 && value is Image image)
            {
                // logger.Debug($"copying MODIFY arg {name}");
                // make sure we have a unique copy
                value = image.Copy().CopyMemory();
            }

            Set(name, value);
        }

        /// <summary>
        /// Set a GObject property. The value is converted to the property type, if possible.
        /// </summary>
        /// <param name="name">The name of the property to set.</param>
        /// <param name="flags">See <see cref="Internal.Enums.VipsArgumentFlags"/>.</param>
        /// <param name="matchImage">A <see cref="Image"/> used as guide.</param>
        /// <param name="value">The value.</param>
        private void Set(string name, Internal.Enums.VipsArgumentFlags flags, Image matchImage, object value)
        {
            // logger.Debug($"Operation.Set: name = {name}, flags = {flags}, " +
            //             $"matchImage = {matchImage} value = {value}");

            // if the object wants an image and we have a constant, Imageize it
            //
            // if the object wants an image array, Imageize any constants in the
            // array
            if (matchImage != null)
            {
                var gtype = GetTypeOf(name);
                if (gtype == GValue.ImageType)
                {
                    value = Image.Imageize(matchImage, value);
                }
                else if (gtype == GValue.ArrayImageType)
                {
                    if (!(value is Array values) || values.Rank != 1)
                    {
                        throw new Exception(
                            $"unsupported value type {value.GetType()} for VipsArrayImage");
                    }

                    var images = new Image[values.Length];
                    for (var i = 0; i < values.Length; i++)
                    {
                        ref var image = ref images[i];
                        image = Image.Imageize(matchImage, values.GetValue(i));
                    }

                    value = images;
                }
            }

            Set(name, flags, value);
        }

        private Internal.Enums.VipsOperationFlags GetFlags()
        {
            return VipsOperation.GetFlags(this);
        }

        // this is slow ... call as little as possible
        private IEnumerable<KeyValuePair<string, Internal.Enums.VipsArgumentFlags>> GetArgs()
        {
            var args = new List<KeyValuePair<string, Internal.Enums.VipsArgumentFlags>>();

            // vips_object_get_args was added in 8.7
            if (NetVips.AtLeastLibvips(8, 7))
            {
                var result = Internal.VipsObject.GetArgs(this, out var names, out var flags, out var nArgs);

                if (result != 0)
                {
                    throw new VipsException("unable to get arguments from operation");
                }

                for (var i = 0; i < nArgs; i++)
                {
                    var flag = (Internal.Enums.VipsArgumentFlags)
                        Marshal.PtrToStructure(flags + i * sizeof(int), typeof(int));
                    if ((flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_CONSTRUCT) == 0)
                    {
                        continue;
                    }

                    var name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(names, i * IntPtr.Size));

                    // libvips uses '-' to separate parts of arg names, but we
                    // need '_' for C#
                    name = name.Replace("-", "_");

                    args.Add(new KeyValuePair<string, Internal.Enums.VipsArgumentFlags>(name, flag));
                }
            }
            else
            {
                IntPtr AddConstruct(IntPtr self, IntPtr pspec, IntPtr argumentClass, IntPtr argumentInstance, IntPtr a,
                    IntPtr b)
                {
                    var flags = argumentClass.Dereference<VipsArgumentClass.Struct>().Flags;
                    if ((flags & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_CONSTRUCT) == 0)
                    {
                        return IntPtr.Zero;
                    }

                    var name = Marshal.PtrToStringAnsi(pspec.Dereference<GParamSpec.Struct>().Name);

                    // libvips uses '-' to separate parts of arg names, but we
                    // need '_' for C#
                    name = name.Replace("-", "_");

                    args.Add(new KeyValuePair<string, Internal.Enums.VipsArgumentFlags>(name, flags));

                    return IntPtr.Zero;
                }

                Vips.ArgumentMap(this, AddConstruct, IntPtr.Zero, IntPtr.Zero);
            }


            return args;
        }

        /// <summary>
        /// Call a libvips operation.
        /// </summary>
        /// <remarks>
        /// Use this method to call any libvips operation. For example:
        /// <code language="lang-csharp">
        /// var blackImage = Operation.Call("black", 10, 10);
        /// </code>
        /// See the Introduction for notes on how this works.
        /// </remarks>
        /// <param name="operationName">Operation name.</param>
        /// <param name="args">An arbitrary number and variety of arguments.</param>
        /// <returns>A new object.</returns>
        public static object Call(string operationName, params object[] args) =>
            Call(operationName, null, null, args);

        /// <summary>
        /// Call a libvips operation.
        /// </summary>
        /// <remarks>
        /// Use this method to call any libvips operation. For example:
        /// <code language="lang-csharp">
        /// var blackImage = Operation.Call("black", 10, 10);
        /// </code>
        /// See the Introduction for notes on how this works.
        /// </remarks>
        /// <param name="operationName">Operation name.</param>
        /// <param name="kwargs">Optional arguments.</param>
        /// <param name="args">An arbitrary number and variety of arguments.</param>
        /// <returns>A new object.</returns>
        public static object Call(string operationName, VOption kwargs = null, params object[] args) =>
            Call(operationName, kwargs, null, args);

        /// <summary>
        /// Call a libvips operation.
        /// </summary>
        /// <remarks>
        /// Use this method to call any libvips operation. For example:
        /// <code language="lang-csharp">
        /// var blackImage = Operation.Call("black", 10, 10);
        /// </code>
        /// See the Introduction for notes on how this works.
        /// </remarks>
        /// <param name="operationName">Operation name.</param>
        /// <param name="kwargs">Optional arguments.</param>
        /// <param name="matchImage">A <see cref="Image"/> used as guide.</param>
        /// <param name="args">An arbitrary number and variety of arguments.</param>
        /// <returns>A new object.</returns>
        public static object Call(string operationName, VOption kwargs = null, Image matchImage = null,
            params object[] args)
        {
            // logger.Debug($"VipsOperation.call: operationName = {operationName}");
            // logger.Debug($"VipsOperation.call: matchImage = {matchImage}");
            // logger.Debug($"VipsOperation.call: args = {args}, kwargs = {kwargs}");

            // pull out the special string_options kwarg
            object stringOptions = null;
            kwargs?.Remove("string_options", out stringOptions);
            // logger.Debug($"VipsOperation.call: stringOptions = {stringOptions}");

            var op = NewFromName(operationName);
            var arguments = op.GetArgs();
            var requiredOutput = new List<string>();
            var optionalOutput = new List<string>();

            // logger.Debug($"VipsOperation.call: arguments = {arguments}");

            // set any string options before any args so they can't be
            // overridden
            if (stringOptions != null && !op.SetString(stringOptions as string))
            {
                throw new VipsException($"unable to call {operationName}");
            }

            var n = 0;
            var memberX = matchImage == null;
            foreach (var entry in arguments)
            {
                var name = entry.Key;
                var flag = entry.Value;

                // fetch required output args, plus modified input images
                if ((flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_OUTPUT) != 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) != 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_DEPRECATED) == 0 ||
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_INPUT) != 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_MODIFY) != 0)
                {
                    requiredOutput.Add(name);
                }

                // fetch and set optional args
                if (kwargs != null &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) == 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_DEPRECATED) == 0 &&
                    kwargs.ContainsKey(name))
                {
                    kwargs.Remove(name, out var value);
                    op.Set(name, flag, matchImage, value);

                    if ((flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_OUTPUT) != 0)
                    {
                        optionalOutput.Add(name);
                    }

                    continue;
                }

                // set required input args
                if ((flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_INPUT) != 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) != 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_DEPRECATED) == 0)
                {
                    // the first required input image arg will be self
                    if (!memberX && op.GetTypeOf(name) == GValue.ImageType)
                    {
                        op.Set(name, flag, matchImage);
                        memberX = true;
                    }
                    else
                    {
                        if (n > args.Length)
                        {
                            break;
                        }

                        op.Set(name, flag, matchImage, args[n]);
                        n++;
                    }
                }
            }

            if (n != args.Length)
            {
                throw new ArgumentException(
                    $"unable to call {operationName}: {args.Length} arguments given, but {n} required");
            }

            if (kwargs != null && kwargs.Count > 0)
            {
                throw new Exception(
                    $"{operationName} does not support argument(s): {string.Join(", ", kwargs.Keys.ToArray())}");
            }

            // build operation
            var vop = VipsOperation.Build(op);
            if (vop == IntPtr.Zero)
            {
                throw new VipsException($"unable to call {operationName}");
            }

            op = new Operation(vop);

            var results = new object[requiredOutput.Count];

            // fetch required output args, plus modified input images
            for (var i = 0; i < requiredOutput.Count; i++)
            {
                var name = requiredOutput[i];

                ref var result = ref results[i];
                result = op.Get(name);
            }

            if (optionalOutput.Count > 0)
            {
                var optionalArgs = new VOption();

                // fetch optional output args
                foreach (var name in optionalOutput)
                {
                    optionalArgs[name] = op.Get(name);
                }

                var resultsLength = results.Length;
                Array.Resize(ref results, resultsLength + 1);
                results[resultsLength] = optionalArgs;
            }

            Internal.VipsObject.UnrefOutputs(op);

            // logger.Debug($"VipsOperation.call: result = {result}");

            return results.Length == 1 ? results[0] : results;
        }

        /// <summary>
        /// Make a C#-style docstring + function declaration.
        /// </summary>
        /// <remarks>
        /// This is used to generate the functions in <see cref="Image"/>.
        /// </remarks>
        /// <param name="operationName">Operation name.</param>
        /// <param name="indent">Indentation level.</param>
        /// <param name="outParameters">The out parameters of this function.</param>
        /// <returns>A C#-style docstring + function declaration.</returns>
        private static string GenerateFunction(string operationName, string indent = "        ",
            IReadOnlyList<string> outParameters = null)
        {
            var op = NewFromName(operationName);
            if ((op.GetFlags() & Internal.Enums.VipsOperationFlags.VIPS_OPERATION_DEPRECATED) != 0)
            {
                throw new Exception($"No such operator. Operator \"{operationName}\" is deprecated");
            }

            // we are only interested in non-deprecated args
            var arguments = op.GetArgs()
                .Where(x => (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_DEPRECATED) == 0)
                .ToList();

            // find the first required input image arg, if any ... that will be self
            string memberX = null;
            foreach (var entry in arguments)
            {
                var name = entry.Key;
                var flag = entry.Value;

                if ((flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_INPUT) != 0 &&
                    (flag & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) != 0 &&
                    op.GetTypeOf(name) == GValue.ImageType)
                {
                    memberX = name;
                    break;
                }
            }

            string[] reservedKeywords =
            {
                "in", "ref", "out", "ushort"
            };

            var requiredInput = arguments.Where(x =>
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_INPUT) != 0 &&
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) != 0 &&
                    x.Key != memberX)
                .Select(x => x.Key)
                .ToArray();
            var optionalInput = arguments.Where(x =>
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_INPUT) != 0 &&
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) == 0)
                .Select(x => x.Key)
                .ToArray();
            var requiredOutput = arguments.Where(x =>
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_OUTPUT) != 0 &&
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) != 0 ||
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_INPUT) != 0 &&
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) != 0 &&
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_MODIFY) != 0)
                .Select(x => x.Key)
                .ToArray();

            string SafeIdentifier(string name) =>
                reservedKeywords.Contains(name)
                    ? "@" + name
                    : name;

            string SafeCast(string type, string name = "result")
            {
                switch (type)
                {
                    case "GObject":
                    case "Image":
                    case "int[]":
                    case "double[]":
                    case "byte[]":
                    case "Image[]":
                    case "object[]":
                        return $" as {type};";
                    case "bool":
                        return $" is {type} {name} && {name};";
                    case "int":
                        return $" is {type} {name} ? {name} : 0;";
                    case "ulong":
                        return $" is {type} {name} ? {name} : 0ul;";
                    case "double":
                        return $" is {type} {name} ? {name} : 0d;";
                    case "string":
                        return $" is {type} {name} ? {name} : null;";
                    default:
                        return ";";
                }
            }

            string ToNullable(string type, string name)
            {
                switch (type)
                {
                    case "Image[]":
                    case "object[]":
                    case "int[]":
                    case "double[]":
                    case "byte[]":
                    case "GObject":
                    case "Image":
                    case "string":
                        return $"{type} {name} = null";
                    case "bool":
                    case "int":
                    case "ulong":
                    case "double":
                        return $"{type}? {name} = null";
                    default:
                        throw new Exception("Unsupported type: " + type);
                }
            }

            string CheckNullable(string type, string name)
            {
                switch (type)
                {
                    case "Image[]":
                    case "object[]":
                    case "int[]":
                    case "double[]":
                    case "byte[]":
                        return $"{name} != null && {name}.Length > 0";
                    case "GObject":
                    case "Image":
                    case "string":
                        return $"{name} != null";
                    case "bool":
                    case "int":
                    case "ulong":
                    case "double":
                        return $"{name}.HasValue";
                    default:
                        throw new Exception("Unsupported type: " + type);
                }
            }

            var result = new StringBuilder($"{indent}/// <summary>\n");

            var newOperationName = operationName.ToPascalCase();

            var description = op.GetDescription();
            result.AppendLine($"{indent}/// {description.FirstLetterToUpper()}.")
                .AppendLine($"{indent}/// </summary>")
                .AppendLine($"{indent}/// <example>")
                .AppendLine($"{indent}/// <code language=\"lang-csharp\">")
                .Append($"{indent}/// ");

            if (requiredOutput.Length == 1)
            {
                var name = requiredOutput[0];
                result.Append(
                    $"{GValue.GTypeToCSharp(op.GetTypeOf(name))} {SafeIdentifier(name).ToPascalCase().FirstLetterToLower()} = ");
            }
            else if (requiredOutput.Length > 1)
            {
                result.Append("var output = ");
            }

            result.Append(memberX ?? "NetVips.Image")
                .Append(
                    $".{newOperationName}({string.Join(", ", requiredInput.Select(x => SafeIdentifier(x).ToPascalCase().FirstLetterToLower()).ToArray())}");

            if (outParameters != null)
            {
                if (requiredInput.Length > 0)
                {
                    result.Append(", ");
                }

                result.Append(
                    $"{string.Join(", ", outParameters.Select(name => $"out var {SafeIdentifier(name).ToPascalCase().FirstLetterToLower()}").ToArray())}");
            }

            if (optionalInput.Length > 0)
            {
                if (requiredInput.Length > 0 || outParameters != null)
                {
                    result.Append(", ");
                }

                for (var i = 0; i < optionalInput.Length; i++)
                {
                    var optionalName = optionalInput[i];
                    result.Append(
                            $"{SafeIdentifier(optionalName).ToPascalCase().FirstLetterToLower()}: {GValue.GTypeToCSharp(op.GetTypeOf(optionalName))}")
                        .Append(i != optionalInput.Length - 1 ? ", " : string.Empty);
                }
            }

            result.AppendLine(");");

            result.AppendLine($"{indent}/// </code>")
                .AppendLine($"{indent}/// </example>");

            foreach (var requiredName in requiredInput)
            {
                result.AppendLine(
                    $"{indent}/// <param name=\"{requiredName.ToPascalCase().FirstLetterToLower()}\">{op.GetBlurb(requiredName)}.</param>");
            }

            if (outParameters != null)
            {
                foreach (var outParameter in outParameters)
                {
                    result.AppendLine(
                        $"{indent}/// <param name=\"{outParameter.ToPascalCase().FirstLetterToLower()}\">{op.GetBlurb(outParameter)}.</param>");
                }
            }

            foreach (var optionalName in optionalInput)
            {
                result.AppendLine(
                    $"{indent}/// <param name=\"{optionalName.ToPascalCase().FirstLetterToLower()}\">{op.GetBlurb(optionalName)}.</param>");
            }

            string outputType;

            var outputTypes = requiredOutput.Select(name => GValue.GTypeToCSharp(op.GetTypeOf(name))).ToArray();
            switch (outputTypes.Length)
            {
                case 0:
                    outputType = "void";
                    break;
                case 1:
                    outputType = outputTypes[0];
                    break;
                default:
                    outputType = outputTypes.Any(o => o != outputTypes[0]) ? $"{outputTypes[0]}[]" : "object[]";
                    break;
            }

            string ToCref(string name) =>
                name.Equals("Image") || name.Equals("GObject") ? $"new <see cref=\"{name}\"/>" : name;

            if (outputType.EndsWith("[]"))
            {
                result.AppendLine(
                    $"{indent}/// <returns>An array of {ToCref(outputType.Remove(outputType.Length - 2))}s.</returns>");
            }
            else if (!outputType.Equals("void"))
            {
                result.AppendLine($"{indent}/// <returns>A {ToCref(outputType)}.</returns>");
            }

            result.Append($"{indent}public ")
                .Append(memberX == null ? "static " : string.Empty)
                .Append(outputType)
                .Append(
                    $" {newOperationName}({string.Join(", ", requiredInput.Select(name => $"{GValue.GTypeToCSharp(op.GetTypeOf(name))} {SafeIdentifier(name).ToPascalCase().FirstLetterToLower()}").ToArray())}");

            if (outParameters != null)
            {
                if (requiredInput.Length > 0)
                {
                    result.Append(", ");
                }

                result.Append(string.Join(", ",
                    outParameters.Select(name =>
                            $"out {GValue.GTypeToCSharp(op.GetTypeOf(name))} {SafeIdentifier(name).ToPascalCase().FirstLetterToLower()}")
                        .ToArray()));
            }

            if (optionalInput.Length > 0)
            {
                if (requiredInput.Length > 0 || outParameters != null)
                {
                    result.Append(", ");
                }

                result.Append(string.Join(", ",
                    optionalInput.Select(name =>
                            $"{ToNullable(GValue.GTypeToCSharp(op.GetTypeOf(name)), SafeIdentifier(name).ToPascalCase().FirstLetterToLower())}")
                        .ToArray()));
            }

            result.AppendLine(")")
                .AppendLine($"{indent}{{");

            if (optionalInput.Length > 0)
            {
                result.AppendLine($"{indent}    var options = new VOption();").AppendLine();

                foreach (var optionalName in optionalInput)
                {
                    var safeIdentifier = SafeIdentifier(optionalName).ToPascalCase().FirstLetterToLower();
                    var optionsName = safeIdentifier == optionalName
                        ? "nameof(" + optionalName + ")"
                        : "\"" + optionalName + "\"";

                    result.Append($"{indent}    if (")
                        .Append(CheckNullable(GValue.GTypeToCSharp(op.GetTypeOf(optionalName)), safeIdentifier))
                        .AppendLine(")")
                        .AppendLine($"{indent}    {{")
                        .AppendLine($"{indent}        options.Add({optionsName}, {safeIdentifier});")
                        .AppendLine($"{indent}    }}")
                        .AppendLine();
                }
            }

            if (outParameters != null)
            {
                if (optionalInput.Length > 0)
                {
                    foreach (var outParameterName in outParameters)
                    {
                        result.AppendLine($"{indent}    options.Add(\"{outParameterName}\", true);");
                    }
                }
                else
                {
                    result.AppendLine($"{indent}    var optionalOutput = new VOption")
                        .AppendLine($"{indent}    {{");
                    for (var i = 0; i < outParameters.Count; i++)
                    {
                        var outParameterName = outParameters[i];
                        result.Append($"{indent}        {{\"{outParameterName}\", true}}")
                            .AppendLine(i != outParameters.Count - 1 ? "," : string.Empty);
                    }

                    result.AppendLine($"{indent}    }};");
                }

                result.AppendLine()
                    .Append($"{indent}    var results = ")
                    .Append(memberX == null ? "Operation" : "this")
                    .Append($".Call(\"{operationName}\"")
                    .Append(optionalInput.Length > 0 ? ", options" : ", optionalOutput");
            }
            else
            {
                result.Append($"{indent}    ");
                if (outputType != "void")
                {
                    result.Append("return ");
                }

                result.Append(memberX == null ? "Operation" : "this")
                    .Append($".Call(\"{operationName}\"");
                if (optionalInput.Length > 0)
                {
                    result.Append(", options");
                }
            }

            if (requiredInput.Length > 0)
            {
                result.Append(", ");

                // Co-variant array conversion from Image[] to object[] can cause run-time exception on write operation.
                // So we need to wrap the image array into a object array.
                var needToWrap = requiredInput.Length == 1 &&
                                 GValue.GTypeToCSharp(op.GetTypeOf(requiredInput[0])).Equals("Image[]");
                if (needToWrap)
                {
                    result.Append("new object[] { ");
                }

                result.Append(string.Join(", ",
                    requiredInput.Select(x => SafeIdentifier(x).ToPascalCase().FirstLetterToLower()).ToArray()));

                if (needToWrap)
                {
                    result.Append(" }");
                }
            }

            result.Append(")");

            if (outParameters != null)
            {
                result.AppendLine(" as object[];");
                if (outputType != "void")
                {
                    result.Append($"{indent}    var finalResult = results?[0]")
                        .Append(SafeCast(outputType));
                }
            }
            else
            {
                result.Append(SafeCast(outputType))
                    .AppendLine();
            }

            if (outParameters != null)
            {
                result.AppendLine()
                    .AppendLine($"{indent}    var opts = results?[1] as VOption;");
                for (var i = 0; i < outParameters.Count; i++)
                {
                    var outParameter = outParameters[i];
                    result.Append(
                            $"{indent}    {SafeIdentifier(outParameter).ToPascalCase().FirstLetterToLower()} = opts?[\"{outParameter}\"]")
                        .AppendLine(SafeCast(GValue.GTypeToCSharp(op.GetTypeOf(outParameter)), $"out{i + 1}"));
                }

                if (outputType != "void")
                {
                    result.AppendLine()
                        .Append($"{indent}    return finalResult;")
                        .AppendLine();
                }
            }

            result.Append($"{indent}}}")
                .AppendLine();

            var optionalOutput = arguments.Where(x =>
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_OUTPUT) != 0 &&
                    (x.Value & Internal.Enums.VipsArgumentFlags.VIPS_ARGUMENT_REQUIRED) == 0)
                .Select(x => x.Key)
                .ToArray();

            // Create method overloading if necessary
            if (optionalOutput.Length > 0 && outParameters == null)
            {
                result.AppendLine()
                    .Append(GenerateFunction(operationName, indent, new[] { optionalOutput[0] }));
            }
            else if (outParameters != null && outParameters.Count != optionalOutput.Length)
            {
                result.AppendLine()
                    .Append(GenerateFunction(operationName, indent,
                        optionalOutput.Take(outParameters.Count + 1).ToArray()));
            }

            return result.ToString();
        }

        private static void AddNickname(IntPtr gtype, ICollection<string> allNickNames)
        {
            var nickname = NetVips.NicknameFind(gtype);
            allNickNames.Add(nickname);

            IntPtr TypeMap(IntPtr type, IntPtr a, IntPtr b)
            {
                AddNickname(type, allNickNames);

                return IntPtr.Zero;
            }

            NetVips.TypeMap(gtype, TypeMap);
        }

        /// <summary>
        /// Generate the `Image.Generated.cs` file.
        /// </summary>
        /// <remarks>
        /// This is used to generate the `Image.Generated.cs` file (<see cref="Image"/>).
        /// Use it with something like:
        /// <code language="lang-csharp">
        /// File.WriteAllText("Image.Generated.cs", Operation.GenerateImageClass());
        /// </code>
        /// </remarks>
        /// <param name="indent">Indentation level.</param>
        /// <returns>The `Image.Generated.cs` as string.</returns>
        public static string GenerateImageClass(string indent = "        ")
        {
            // generate list of all nicknames we can generate docstrings for
            var allNickNames = new List<string>();

            IntPtr TypeMap(IntPtr type, IntPtr a, IntPtr b)
            {
                AddNickname(type, allNickNames);

                return IntPtr.Zero;
            }

            NetVips.TypeMap(NetVips.TypeFromName("VipsOperation"), TypeMap);

            // Sort
            allNickNames.Sort();

            // Filter duplicates
            allNickNames = allNickNames.Distinct().ToList();

            // remove operations we have to wrap by hand
            var exclude = new[]
            {
                "scale",
                "ifthenelse",
                "bandjoin",
                "bandrank",
                "composite"
            };

            allNickNames = allNickNames.Where(x => !exclude.Contains(x)).ToList();

            const string preamble = @"//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     libvips version: {0}
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------";

            var stringBuilder =
                new StringBuilder(string.Format(preamble,
                    $"{NetVips.Version(0)}.{NetVips.Version(1)}.{NetVips.Version(2)}"));
            stringBuilder.AppendLine()
                .AppendLine()
                .AppendLine("namespace NetVips")
                .AppendLine("{")
                .AppendLine("    public sealed partial class Image")
                .AppendLine("    {")
                .AppendLine($"{indent}#region auto-generated functions")
                .AppendLine();
            foreach (var nickname in allNickNames)
            {
                try
                {
                    stringBuilder.AppendLine(GenerateFunction(nickname, indent));
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            stringBuilder.AppendLine($"{indent}#endregion")
                .AppendLine()
                .AppendLine($"{indent}#region auto-generated properties")
                .AppendLine();

            var tmpFile = Image.NewTempFile("%s.v");
            var allProperties = tmpFile.GetFields();
            foreach (var property in allProperties)
            {
                var type = GValue.GTypeToCSharp(tmpFile.GetTypeOf(property));
                stringBuilder.AppendLine($"{indent}/// <summary>")
                    .AppendLine($"{indent}/// {tmpFile.GetBlurb(property)}")
                    .AppendLine($"{indent}/// </summary>")
                    .AppendLine($"{indent}public {type} {property.ToPascalCase()} => ({type})Get(\"{property}\");")
                    .AppendLine();
            }

            stringBuilder.AppendLine($"{indent}#endregion")
                .AppendLine("    }")
                .AppendLine("}");

            return stringBuilder.ToString();
        }
    }
}