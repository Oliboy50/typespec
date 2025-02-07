// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.Generator.CSharp.Expressions;
using Microsoft.Generator.CSharp.Input;

namespace Microsoft.Generator.CSharp
{
    public abstract class EnumTypeProvider : TypeProvider
    {
        public static EnumTypeProvider Create(InputEnumType input, SourceInputModel? sourceInputModel)
            => input.IsExtensible
            ? new ExtensibleEnumTypeProvider(input, sourceInputModel)
            : new FixedEnumTypeProvider(input, sourceInputModel);

        protected EnumTypeProvider(InputEnumType input, SourceInputModel? sourceInputModel) : base(sourceInputModel)
        {
            _deprecated = input.Deprecated;

            IsExtensible = input.IsExtensible;
            Name = input.Name.ToCleanName();
            Namespace = GetDefaultModelNamespace(CodeModelPlugin.Instance.Configuration.Namespace);
            ValueType = CodeModelPlugin.Instance.TypeFactory.CreateCSharpType(input.EnumValueType);
            IsStringValueType = ValueType.Equals(typeof(string));
            IsIntValueType = ValueType.Equals(typeof(int)) || ValueType.Equals(typeof(long));
            IsFloatValueType = ValueType.Equals(typeof(float)) || ValueType.Equals(typeof(double));
            IsNumericValueType = IsIntValueType || IsFloatValueType;

            Description = input.Description != null ? FormattableStringHelpers.FromString(input.Description) : FormattableStringHelpers.Empty;
        }

        public CSharpType ValueType { get; }
        public bool IsExtensible { get; }
        internal bool IsIntValueType { get; }
        internal bool IsFloatValueType { get; }
        internal bool IsStringValueType { get; }
        internal bool IsNumericValueType { get; }
        public override string Name { get; }
        public override string Namespace { get; }
        public override FormattableString Description { get; }

        /// <summary>
        /// The serialization provider for this enum.
        /// </summary>
        public TypeProvider? Serialization { get; protected init; }

        private IReadOnlyList<EnumTypeMember>? _members;
        public IReadOnlyList<EnumTypeMember> Members => _members ??= BuildMembers();

        protected abstract IReadOnlyList<EnumTypeMember> BuildMembers();

        public abstract ValueExpression ToSerial(ValueExpression enumExpression);

        public abstract ValueExpression ToEnum(ValueExpression valueExpression);
    }
}
