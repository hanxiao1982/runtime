// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Reflection;

namespace System.Text.Json.Serialization.Metadata
{
    /// <summary>
    /// Represents a strongly-typed property to prevent boxing and to create a direct delegate to the getter\setter.
    /// </summary>
    /// <typeparamref name="T"/> is the <see cref="JsonConverter{T}.TypeToConvert"/> for either the property's converter,
    /// or a type's converter, if the current instance is a <see cref="JsonTypeInfo.PropertyInfoForTypeInfo"/>.
    internal sealed class JsonPropertyInfo<T> : JsonPropertyInfo
    {
        /// <summary>
        /// Returns true if the property's converter is external (a user's custom converter)
        /// and the type to convert is not the same as the declared property type (polymorphic).
        /// Used to determine whether to perform additional validation on the value returned by the
        /// converter on deserialization.
        /// </summary>
        private bool _converterIsExternalAndPolymorphic;

        // Since a converter's TypeToConvert (which is the T value in this type) can be different than
        // the property's type, we track that and whether the property type can be null.
        private bool _propertyTypeEqualsTypeToConvert;

        internal Func<object, T>? Get { get; set; }

        internal Action<object, T>? Set { get; set; }

        internal override object? DefaultValue => default(T);

        public JsonConverter<T> Converter { get; internal set; } = null!;

        internal override void Initialize(
            Type parentClassType,
            Type declaredPropertyType,
            ConverterStrategy converterStrategy,
            MemberInfo? memberInfo,
            bool isVirtual,
            JsonConverter converter,
            JsonIgnoreCondition? ignoreCondition,
            JsonSerializerOptions options,
            JsonTypeInfo? jsonTypeInfo = null)
        {
            Debug.Assert(converter != null);

            PropertyType = declaredPropertyType;
            ConverterStrategy = converterStrategy;
            if (jsonTypeInfo != null)
            {
                JsonTypeInfo = jsonTypeInfo;
            }

            ConverterBase = converter;
            Options = options;
            DeclaringType = parentClassType;
            MemberInfo = memberInfo;
            IsVirtual = isVirtual;

            if (memberInfo != null)
            {
                switch (memberInfo)
                {
                    case PropertyInfo propertyInfo:
                        {
                            bool useNonPublicAccessors = GetAttribute<JsonIncludeAttribute>(propertyInfo) != null;

                            MethodInfo? getMethod = propertyInfo.GetMethod;
                            if (getMethod != null && (getMethod.IsPublic || useNonPublicAccessors))
                            {
                                HasGetter = true;
                                Get = options.MemberAccessorStrategy.CreatePropertyGetter<T>(propertyInfo);
                            }

                            MethodInfo? setMethod = propertyInfo.SetMethod;
                            if (setMethod != null && (setMethod.IsPublic || useNonPublicAccessors))
                            {
                                HasSetter = true;
                                Set = options.MemberAccessorStrategy.CreatePropertySetter<T>(propertyInfo);
                            }

                            MemberType = MemberTypes.Property;

                            break;
                        }

                    case FieldInfo fieldInfo:
                        {
                            Debug.Assert(fieldInfo.IsPublic);

                            HasGetter = true;
                            Get = options.MemberAccessorStrategy.CreateFieldGetter<T>(fieldInfo);

                            if (!fieldInfo.IsInitOnly)
                            {
                                HasSetter = true;
                                Set = options.MemberAccessorStrategy.CreateFieldSetter<T>(fieldInfo);
                            }

                            MemberType = MemberTypes.Field;

                            break;
                        }

                    default:
                        {
                            Debug.Fail($"Invalid memberInfo type: {memberInfo.GetType().FullName}");
                            break;
                        }
                }

                // TODO (perf): can we pre-compute some of these values during source gen?
                _converterIsExternalAndPolymorphic = !converter.IsInternalConverter && PropertyType != converter.TypeToConvert;
                PropertyTypeCanBeNull = PropertyType.CanBeNull();
                _propertyTypeEqualsTypeToConvert = typeof(T) == PropertyType;

                GetPolicies(ignoreCondition);
            }
            else
            {
                IsForTypeInfo = true;
                HasGetter = true;
                HasSetter = true;
            }
        }

        internal void InitializeForSourceGen(JsonSerializerOptions options, JsonPropertyInfoValues<T> propertyInfo)
        {
            Options = options;
            ClrName = propertyInfo.PropertyName;

            // Property name settings.
            if (propertyInfo.JsonPropertyName != null)
            {
                NameAsString = propertyInfo.JsonPropertyName;
            }
            else if (options.PropertyNamingPolicy == null)
            {
                NameAsString = ClrName;
            }
            else
            {
                NameAsString = options.PropertyNamingPolicy.ConvertName(ClrName);
                if (NameAsString == null)
                {
                    ThrowHelper.ThrowInvalidOperationException_SerializerPropertyNameNull(DeclaringType, this);
                }
            }

            NameAsUtf8Bytes ??= Encoding.UTF8.GetBytes(NameAsString!);
            EscapedNameSection ??= JsonHelpers.GetEscapedPropertyNameSection(NameAsUtf8Bytes, Options.Encoder);
            SrcGen_IsPublic = propertyInfo.IsPublic;
            SrcGen_HasJsonInclude = propertyInfo.HasJsonInclude;
            SrcGen_IsExtensionData = propertyInfo.IsExtensionData;
            PropertyType = typeof(T);

            JsonTypeInfo propertyTypeInfo = propertyInfo.PropertyTypeInfo;
            Type declaringType = propertyInfo.DeclaringType;

            JsonConverter? converter = propertyInfo.Converter;
            if (converter == null)
            {
                converter = propertyTypeInfo.PropertyInfoForTypeInfo.ConverterBase as JsonConverter<T>;
                if (converter == null)
                {
                    throw new InvalidOperationException(SR.Format(SR.ConverterForPropertyMustBeValid, declaringType, ClrName, typeof(T)));
                }
            }

            ConverterBase = converter;

            if (propertyInfo.IgnoreCondition == JsonIgnoreCondition.Always)
            {
                IsIgnored = true;
                Debug.Assert(!ShouldSerialize);
                Debug.Assert(!ShouldDeserialize);
            }
            else
            {
                Get = propertyInfo.Getter!;
                Set = propertyInfo.Setter;
                HasGetter = Get != null;
                HasSetter = Set != null;
                JsonTypeInfo = propertyTypeInfo;
                DeclaringType = declaringType;
                IgnoreCondition = propertyInfo.IgnoreCondition;
                MemberType = propertyInfo.IsProperty ? MemberTypes.Property : MemberTypes.Field;

                _converterIsExternalAndPolymorphic = !ConverterBase.IsInternalConverter && PropertyType != ConverterBase.TypeToConvert;
                PropertyTypeCanBeNull = typeof(T).CanBeNull();
                _propertyTypeEqualsTypeToConvert = ConverterBase.TypeToConvert == typeof(T);
                ConverterStrategy = Converter!.ConverterStrategy;
                DetermineIgnoreCondition(IgnoreCondition);
                NumberHandling = propertyInfo.NumberHandling;
                DetermineSerializationCapabilities(IgnoreCondition);
            }
        }

        internal override JsonConverter ConverterBase
        {
            get
            {
                return Converter;
            }
            set
            {
                Debug.Assert(value is JsonConverter<T>);
                Converter = (JsonConverter<T>)value;
            }
        }

        internal override object? GetValueAsObject(object obj)
        {
            if (IsForTypeInfo)
            {
                return obj;
            }

            Debug.Assert(HasGetter);
            return Get!(obj);
        }

        internal override bool GetMemberAndWriteJson(object obj, ref WriteStack state, Utf8JsonWriter writer)
        {
            T value = Get!(obj);

            if (
#if NETCOREAPP
                !typeof(T).IsValueType && // treated as a constant by recent versions of the JIT.
#else
                !Converter.IsValueType &&
#endif
                Options.ReferenceHandlingStrategy == ReferenceHandlingStrategy.IgnoreCycles &&
                value is not null &&
                !state.IsContinuation &&
                // .NET types that are serialized as JSON primitive values don't need to be tracked for cycle detection e.g: string.
                ConverterStrategy != ConverterStrategy.Value &&
                state.ReferenceResolver.ContainsReferenceForCycleDetection(value))
            {
                // If a reference cycle is detected, treat value as null.
                value = default!;
                Debug.Assert(value == null);
            }

            if (IgnoreDefaultValuesOnWrite)
            {
                // If value is null, it is a reference type or nullable<T>.
                if (value == null)
                {
                    return true;
                }

                if (!PropertyTypeCanBeNull)
                {
                    if (_propertyTypeEqualsTypeToConvert)
                    {
                        // The converter and property types are the same, so we can use T for EqualityComparer<>.
                        if (EqualityComparer<T>.Default.Equals(default, value))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        Debug.Assert(JsonTypeInfo.Type == PropertyType);

                        // Use a late-bound call to EqualityComparer<DeclaredPropertyType>.
                        if (JsonTypeInfo.DefaultValueHolder.IsDefaultValue(value))
                        {
                            return true;
                        }
                    }
                }
            }

            if (value == null)
            {
                Debug.Assert(PropertyTypeCanBeNull);

                if (Converter.HandleNullOnWrite)
                {
                    if (state.Current.PropertyState < StackFramePropertyState.Name)
                    {
                        state.Current.PropertyState = StackFramePropertyState.Name;
                        writer.WritePropertyNameSection(EscapedNameSection);
                    }

                    int originalDepth = writer.CurrentDepth;
                    Converter.Write(writer, value, Options);
                    if (originalDepth != writer.CurrentDepth)
                    {
                        ThrowHelper.ThrowJsonException_SerializationConverterWrite(Converter);
                    }
                }
                else
                {
                    writer.WriteNullSection(EscapedNameSection);
                }

                return true;
            }
            else
            {
                if (state.Current.PropertyState < StackFramePropertyState.Name)
                {
                    state.Current.PropertyState = StackFramePropertyState.Name;
                    writer.WritePropertyNameSection(EscapedNameSection);
                }

                return Converter.TryWrite(writer, value, Options, ref state);
            }
        }

        internal override bool GetMemberAndWriteJsonExtensionData(object obj, ref WriteStack state, Utf8JsonWriter writer)
        {
            bool success;
            T value = Get!(obj);

            if (value == null)
            {
                success = true;
            }
            else
            {
                success = Converter.TryWriteDataExtensionProperty(writer, value, Options, ref state);
            }

            return success;
        }

        internal override bool ReadJsonAndSetMember(object obj, ref ReadStack state, ref Utf8JsonReader reader)
        {
            bool success;

            bool isNullToken = reader.TokenType == JsonTokenType.Null;
            if (isNullToken && !Converter.HandleNullOnRead && !state.IsContinuation)
            {
                if (!PropertyTypeCanBeNull)
                {
                    ThrowHelper.ThrowJsonException_DeserializeUnableToConvertValue(Converter.TypeToConvert);
                }

                Debug.Assert(default(T) == null);

                if (!IgnoreDefaultValuesOnRead)
                {
                    T? value = default;
                    Set!(obj, value!);
                }

                success = true;
            }
            else if (Converter.CanUseDirectReadOrWrite && state.Current.NumberHandling == null)
            {
                // CanUseDirectReadOrWrite == false when using streams
                Debug.Assert(!state.IsContinuation);

                if (!isNullToken || !IgnoreDefaultValuesOnRead || !PropertyTypeCanBeNull)
                {
                    // Optimize for internal converters by avoiding the extra call to TryRead.
                    T? fastValue = Converter.Read(ref reader, PropertyType, Options);
                    Set!(obj, fastValue!);
                }

                success = true;
            }
            else
            {
                success = true;
                if (!isNullToken || !IgnoreDefaultValuesOnRead || !PropertyTypeCanBeNull || state.IsContinuation)
                {
                    success = Converter.TryRead(ref reader, PropertyType, Options, ref state, out T? value);
                    if (success)
                    {
#if !DEBUG
                        if (_converterIsExternalAndPolymorphic)
#endif
                        {
                            if (value != null)
                            {
                                Type typeOfValue = value.GetType();
                                if (!PropertyType.IsAssignableFrom(typeOfValue))
                                {
                                    ThrowHelper.ThrowInvalidCastException_DeserializeUnableToAssignValue(typeOfValue, PropertyType);
                                }
                            }
                            else if (!PropertyTypeCanBeNull)
                            {
                                ThrowHelper.ThrowInvalidOperationException_DeserializeUnableToAssignNull(PropertyType);
                            }
                        }

                        Set!(obj, value!);
                    }
                }
            }

            return success;
        }

        internal override bool ReadJsonAsObject(ref ReadStack state, ref Utf8JsonReader reader, out object? value)
        {
            bool success;
            bool isNullToken = reader.TokenType == JsonTokenType.Null;
            if (isNullToken && !Converter.HandleNullOnRead && !state.IsContinuation)
            {
                if (!PropertyTypeCanBeNull)
                {
                    ThrowHelper.ThrowJsonException_DeserializeUnableToConvertValue(Converter.TypeToConvert);
                }

                value = default(T);
                success = true;
            }
            else
            {
                // Optimize for internal converters by avoiding the extra call to TryRead.
                if (Converter.CanUseDirectReadOrWrite && state.Current.NumberHandling == null)
                {
                    // CanUseDirectReadOrWrite == false when using streams
                    Debug.Assert(!state.IsContinuation);

                    value = Converter.Read(ref reader, PropertyType, Options);
                    success = true;
                }
                else
                {
                    success = Converter.TryRead(ref reader, PropertyType, Options, ref state, out T? typedValue);
                    value = typedValue;
                }
            }

            return success;
        }

        internal override void SetExtensionDictionaryAsObject(object obj, object? extensionDict)
        {
            Debug.Assert(HasSetter);
            T typedValue = (T)extensionDict!;
            Set!(obj, typedValue);
        }
    }
}
