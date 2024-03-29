﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Host.Bindings
{
    // Regular BindToInput has to do a TAttribute --> Value creation.
    // But triggers already have a Listener that provided the initial object; and we're just
    // converting it to the user's target parameter.
    internal class TriggerAdapterBindingProvider<TAttribute, TTriggerValue> :
        FluentBindingProvider<TAttribute>, IBindingProvider, IBindingRuleProvider
            where TAttribute : Attribute
            where TTriggerValue : class
    {
        private readonly INameResolver _nameResolver;
        private readonly IConverterManager _converterManager;

        public TriggerAdapterBindingProvider(
            INameResolver nameResolver,
            IConverterManager converterManager)
        {
            _nameResolver = nameResolver;
            _converterManager = converterManager;
        }

        // Listed in precedence for providing via DefaultType.
        // Precedence is more important than how we produce the default type (a direct conversion vs. a converter)
        private static readonly Type[] _defaultTypes = new Type[] {
            typeof(byte[]),
            typeof(JObject),
            typeof(JArray),
            typeof(string)
        };

        // Listed in precedence for providing via DefaultType.
        // Precedence is more important than how we produce the default type (a direct conversion vs. a converter)
        private static readonly Type[] _defaultBatchTypes = new Type[] {
            typeof(byte[][]),
            typeof(JObject[]),
            typeof(JArray),
            typeof(string[])
        };

        // TODO: Refactor GetDefaultType so that a) we don't need a special case for ParameterBindingData
        // and b) we are taking `requestedType` into account when checking if we have a converter
        // GitHub issue: https://github.com/Azure/azure-webjobs-sdk/issues/2930
        public Type GetDefaultType(Attribute attribute, FileAccess access, Type requestedType)
        {
            // If the requestedType is a batch type, we need to return the batch version of default types
            IEnumerable<Type> targets = requestedType.IsArray && !_defaultTypes.Contains(requestedType)
                ? _defaultBatchTypes
                : _defaultTypes;

            if (requestedType == typeof(ParameterBindingData) || requestedType == typeof(ParameterBindingData[]))
            {
                if (_converterManager.HasConverter<TAttribute>(typeof(TTriggerValue), requestedType))
                {
                    return requestedType;
                }

                // We should only be requesting ParameterBindingData if it is supported by the
                // extension we're binding against. In theory, this should never happen.
                throw new InvalidOperationException($"Converter for {requestedType} not found.");
            }

            foreach (var target in targets)
            {
                if (_converterManager.HasConverter<TAttribute>(typeof(TTriggerValue), target))
                {
                    return target;
                }
            }

            if (requestedType.IsArray)
            {
                return typeof(object[]);
            }

            return typeof(object);
        }

        public IEnumerable<BindingRule> GetRules()
        {
            if (typeof(TTriggerValue).IsPublic)
            {
                yield return new BindingRule
                {
                    SourceAttribute = typeof(TAttribute),
                    UserType = OpenType.FromType<TTriggerValue>()
                };
            }

            var cm = (ConverterManager)_converterManager;
            var types = cm.GetPossibleDestinationTypesFromSource(typeof(TAttribute), typeof(TTriggerValue));

            var converters = new Type[] { typeof(TTriggerValue) };

            foreach (OpenType type in types)
            {
                yield return new BindingRule
                {
                    SourceAttribute = typeof(TAttribute),
                    Converters = converters,
                    UserType = type
                };
            }
        }

        public Task<IBinding> TryCreateAsync(BindingProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            var parameter = context.Parameter;
            var parameterType = parameter.ParameterType;

            if (parameterType.IsByRef)
            {
                return Task.FromResult<IBinding>(null);
            }

            var binding = ExactBinding.TryBuild(this, context);

            return Task.FromResult<IBinding>(binding);
        }

        private class ExactBinding : IBinding
        {
            private FuncAsyncConverter _converter;

            private FuncAsyncConverter<DirectInvokeString, TTriggerValue> _directInvoker;
            private FuncAsyncConverter<TTriggerValue, DirectInvokeString> _getInvokeString;
            private Type _userType;

            private TAttribute _attribute;

            public bool FromAttribute => true;

            public static ExactBinding TryBuild(
                TriggerAdapterBindingProvider<TAttribute, TTriggerValue> parent,
                BindingProviderContext context)
            {
                var parameter = context.Parameter;
                var userType = parameter.ParameterType;
                IConverterManager cm = parent._converterManager;

                var converter = cm.GetConverter<TAttribute>(typeof(TTriggerValue), userType);

                if (converter == null)
                {
                    return null;
                }

                var attributeSource = TypeUtility.GetResolvedAttribute<TAttribute>(parameter);

                return new ExactBinding
                {
                    _converter = converter,
                    _directInvoker = GetDirectInvoker(cm),
                    _getInvokeString = GetDirectInvokeString(cm),
                    _attribute = attributeSource,
                    _userType = userType
                };
            }

            private static FuncAsyncConverter<TTriggerValue, DirectInvokeString> GetDirectInvokeString(IConverterManager cm)
            {
                var converter = cm.GetConverter<TTriggerValue, DirectInvokeString, TAttribute>();
                if (converter != null)
                {
                    return converter;
                }

                var strConverter = cm.GetConverter<TTriggerValue, string, TAttribute>();
                if (strConverter != null)
                {
                    return async (input, attr, ctx) =>
                    {
                        var str = await strConverter(input, attr, ctx);
                        return new DirectInvokeString(str);
                    };
                }

                // No converter. Provide something to display in UI
                return (input, attr, ctx) => Task.FromResult(DirectInvokeString.None);
            }

            private static FuncAsyncConverter<DirectInvokeString, TTriggerValue> GetDirectInvoker(IConverterManager cm)
            {
                var direct = cm.GetConverter<DirectInvokeString, TTriggerValue, TAttribute>();
                if (direct != null)
                {
                    return direct;
                }

                var str = cm.GetConverter<string, TTriggerValue, TAttribute>();
                if (str != null)
                {
                    return (input, attr, ctx) => str(input.Value, attr, ctx);
                }
                return null;
            }

            public async Task<IValueProvider> BindAsync(object value, ValueBindingContext context)
            {
                TTriggerValue val = value as TTriggerValue;
                if (val == null)
                {
                    if (_directInvoker != null && (value is string str))
                    {
                        // Direct invoke case. Need to convert String-->TTriggerValue.
                        val = await _directInvoker(new DirectInvokeString(str), _attribute, context);
                    }
                    else
                    {
                        // How is this possible?
                        throw new NotImplementedException();
                    }
                }

                var result = await _converter(val, _attribute, context);

                DirectInvokeString invokeString = await _getInvokeString(val, _attribute, context);
                IValueProvider vp = new ConstantValueProvider(result, _userType, invokeString.Value);
                return vp;
            }

            public Task<IValueProvider> BindAsync(BindingContext context)
            {
                // Never called, since a trigger already has an object.
                throw new NotImplementedException();
            }

            public ParameterDescriptor ToParameterDescriptor()
            {
                return new ParameterDescriptor();
            }
        }
    }
}
