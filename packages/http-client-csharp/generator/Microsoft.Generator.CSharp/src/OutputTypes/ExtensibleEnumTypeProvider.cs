// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.Generator.CSharp.Expressions;
using Microsoft.Generator.CSharp.Input;
using static Microsoft.Generator.CSharp.Expressions.Snippets;

namespace Microsoft.Generator.CSharp
{
    public class ExtensibleEnumTypeProvider : EnumTypeProvider
    {
        private readonly IReadOnlyList<InputEnumTypeValue> _allowedValues;
        private readonly TypeSignatureModifiers _modifiers;

        protected internal ExtensibleEnumTypeProvider(InputEnumType input, SourceInputModel? sourceInputModel) : base(input, sourceInputModel)
        {
            _allowedValues = input.AllowedValues;

            // extensible enums are implemented as readonly structs
            _modifiers = TypeSignatureModifiers.Partial | TypeSignatureModifiers.ReadOnly | TypeSignatureModifiers.Struct;
            if (input.Accessibility == "internal")
            {
                _modifiers |= TypeSignatureModifiers.Internal;
            }

            _valueField = new FieldDeclaration(FieldModifiers.Private | FieldModifiers.ReadOnly, ValueType, "_value");
        }

        private readonly FieldDeclaration _valueField;

        protected override TypeSignatureModifiers GetDeclarationModifiers() => _modifiers;

        protected override IReadOnlyList<EnumTypeMember> BuildMembers()
        {
            var values = new EnumTypeMember[_allowedValues.Count];

            for (int i = 0; i < _allowedValues.Count; i++)
            {
                var inputValue = _allowedValues[i];
                // build the field
                var modifiers = FieldModifiers.Private | FieldModifiers.Const;
                // the fields for extensible enums are private and const, storing the underlying values, therefore we need to append the word `Value` to the name
                var valueName = inputValue.Name.ToCleanName();
                var name = $"{valueName}Value";
                // for initializationValue, if the enum is extensible, we always need it
                var initializationValue = Literal(inputValue.Value);
                var field = new FieldDeclaration(
                    Description: FormattableStringHelpers.FromString(inputValue.Description),
                    Modifiers: modifiers,
                    Type: ValueType,
                    Name: name,
                    InitializationValue: initializationValue);

                values[i] = new EnumTypeMember(valueName, field, inputValue.Value);
            }

            return values;
        }

        protected override CSharpType[] BuildImplements()
            => [new CSharpType(typeof(IEquatable<>), Type)]; // extensible enums implement IEquatable<Self>

        protected override FieldDeclaration[] BuildFields()
            => [_valueField, .. Members.Select(v => v.Field)];

        protected override PropertyDeclaration[] BuildProperties()
        {
            var properties = new PropertyDeclaration[Members.Count];

            var index = 0;
            foreach (var enumValue in Members)
            {
                var name = enumValue.Name;
                var value = enumValue.Value;
                var field = enumValue.Field;
                properties[index++] = new PropertyDeclaration(
                    description: field.Description,
                    modifiers: MethodSignatureModifiers.Public | MethodSignatureModifiers.Static,
                    type: Type,
                    name: name,
                    body: new AutoPropertyBody(false, InitializationExpression: New.Instance(Type, field)));
            }

            return properties;
        }

        protected override CSharpMethod[] BuildConstructors()
        {
            var validation = ValueType.IsValueType ? ValidationType.None : ValidationType.AssertNotNull;
            var valueParameter = new Parameter("value", $"The value.", ValueType)
            {
                Validation = validation
            };
            var signature = new ConstructorSignature(
                Type: Type,
                Summary: null,
                Description: $"Initializes a new instance of {Type:C}.",
                Modifiers: MethodSignatureModifiers.Public,
                Parameters: [valueParameter]);

            var valueField = (ValueExpression)_valueField;
            var body = new MethodBodyStatement[]
            {
                new ParameterValidationBlock(signature.Parameters),
                Assign(valueField, valueParameter)
            };

            return [new CSharpMethod(signature, body, CSharpMethodKinds.Constructor)];
        }

        protected override CSharpMethod[] BuildMethods()
        {
            var methods = new List<CSharpMethod>();

            var leftParameter = new Parameter("left", $"The left value to compare.", Type);
            var rightParameter = new Parameter("right", $"The right value to compare.", Type);
            var left = (ValueExpression)leftParameter;
            var right = (ValueExpression)rightParameter;
            var equalitySignature = new MethodSignature(
                Name: "==",
                Summary: null,
                Description: $"Determines if two {Type:C} values are the same.",
                Modifiers: MethodSignatureModifiers.Public | MethodSignatureModifiers.Static | MethodSignatureModifiers.Operator,
                ReturnType: typeof(bool),
                ReturnDescription: null,
                Parameters: [leftParameter, rightParameter]);

            methods.Add(new(equalitySignature, left.InvokeEquals(right)));

            var inequalitySignature = equalitySignature with
            {
                Name = "!=",
                Description = $"Determines if two {Type:C} values are not the same.",
            };

            methods.Add(new(inequalitySignature, Not(left.InvokeEquals(right))));

            var valueParameter = new Parameter("value", $"The value.", ValueType);
            var castSignature = new MethodSignature(
                Name: string.Empty,
                Summary: null,
                Description: $"Converts a string to a {Type:C}",
                Modifiers: MethodSignatureModifiers.Public | MethodSignatureModifiers.Static | MethodSignatureModifiers.Implicit | MethodSignatureModifiers.Operator,
                ReturnType: Type,
                ReturnDescription: null,
                Parameters: [valueParameter]);

            methods.Add(new(castSignature, New.Instance(Type, valueParameter)));

            var objParameter = new Parameter("obj", $"The object to compare.", typeof(object));
            var equalsSignature = new MethodSignature(
                Name: nameof(object.Equals),
                Summary: null,
                Description: null,
                Modifiers: MethodSignatureModifiers.Public | MethodSignatureModifiers.Override,
                ReturnType: typeof(bool),
                ReturnDescription: null,
                Parameters: [objParameter],
                Attributes: [new CSharpAttribute(typeof(EditorBrowsableAttribute), FrameworkEnumValue(EditorBrowsableState.Never))]);

            // writes the method:
            // public override bool Equals(object obj) => obj is EnumType other && Equals(other);
            methods.Add(new(equalsSignature, And(Is(objParameter, new DeclarationExpression(Type, "other", out var other)), new BoolExpression(new InvokeInstanceMethodExpression(null, nameof(object.Equals), [other])))));

            var otherParameter = new Parameter("other", $"The instance to compare.", Type);
            equalsSignature = equalsSignature with
            {
                Modifiers = MethodSignatureModifiers.Public,
                Parameters = [otherParameter],
                Attributes = Array.Empty<CSharpAttribute>()
            };

            // writes the method:
            // public bool Equals(EnumType other) => string.Equals(_value, other._value, StringComparison.InvariantCultureIgnoreCase);
            // or
            // public bool Equals(EnumType other) => int/float.Equals(_value, other._value);
            var valueField = new TypedValueExpression(ValueType.WithNullable(!ValueType.IsValueType), _valueField);
            var otherValue = ((ValueExpression)otherParameter).Property(_valueField.Name);
            var equalsExpressionBody = IsStringValueType
                            ? new InvokeStaticMethodExpression(ValueType, nameof(object.Equals), [valueField, otherValue, FrameworkEnumValue(StringComparison.InvariantCultureIgnoreCase)])
                            : new InvokeStaticMethodExpression(ValueType, nameof(object.Equals), [valueField, otherValue]);
            methods.Add(new(equalsSignature, equalsExpressionBody));

            var getHashCodeSignature = new MethodSignature(
                Name: nameof(object.GetHashCode),
                Summary: null,
                Description: null,
                Modifiers: MethodSignatureModifiers.Public | MethodSignatureModifiers.Override,
                ReturnType: typeof(int),
                ReturnDescription: null,
                Parameters: Array.Empty<Parameter>());

            // writes the method:
            // for string
            // public override int GetHashCode() => _value?.GetHashCode() ?? 0;
            // for others
            // public override int GetHashCode() => _value.GetHashCode();
            var getHashCodeExpressionBody = IsStringValueType
                            ? NullCoalescing(valueField.NullConditional().InvokeGetHashCode(), Int(0))
                            : valueField.InvokeGetHashCode();
            methods.Add(new(getHashCodeSignature, getHashCodeExpressionBody));

            var toStringSignature = new MethodSignature(
                Name: nameof(object.ToString),
                Summary: null,
                Description: null,
                Modifiers: MethodSignatureModifiers.Public | MethodSignatureModifiers.Override,
                ReturnType: typeof(string),
                ReturnDescription: null,
                Parameters: Array.Empty<Parameter>());

            // writes the method:
            // for string
            // public override string ToString() => _value;
            // for others
            // public override string ToString() => _value.ToString(CultureInfo.InvariantCulture);
            ValueExpression toStringExpressionBody = IsStringValueType
                            ? valueField
                            : valueField.Invoke(nameof(object.ToString), new MemberExpression(typeof(CultureInfo), nameof(CultureInfo.InvariantCulture)));
            methods.Add(new(toStringSignature, toStringExpressionBody));

            // for string-based extensible enums, we are using `ToString` as its serialization
            // for non-string-based extensible enums, we need a method to serialize them
            if (!IsStringValueType)
            {
                var toSerialSignature = new MethodSignature(
                    Name: $"ToSerial{ValueType.Name}",
                    Modifiers: MethodSignatureModifiers.Internal,
                    ReturnType: ValueType,
                    Parameters: Array.Empty<Parameter>(),
                    Summary: null, Description: null, ReturnDescription: null);

                // writes the method:
                // internal float ToSerialSingle() => _value; // when ValueType is float
                // internal int ToSerialInt32() => _value; // when ValueType is int
                // etc
                methods.Add(new(toSerialSignature, valueField));
            }

            return methods.ToArray();
        }

        public override ValueExpression ToSerial(ValueExpression enumExpression)
        {
            var serialMethodName = IsStringValueType ? nameof(object.ToString) : $"ToSerial{ValueType.Name}";
            return enumExpression.Invoke(serialMethodName);
        }

        public override ValueExpression ToEnum(ValueExpression valueExpression)
            => New.Instance(Type, valueExpression);
    }
}
