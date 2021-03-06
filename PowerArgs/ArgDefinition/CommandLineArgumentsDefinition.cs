﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

namespace PowerArgs
{
    /// <summary>
    /// This is the root class used to define a program's command line arguments.  You can start with an empty definition and 
    /// programatically add arguments or you can start from a Type that you have defined and have the definition inferred from it.
    /// </summary>
    public class CommandLineArgumentsDefinition
    {
        /// <summary>
        /// The type that was used to generate this definition.  This will only be populated if you use the constructor that takes in a type and the definition is inferred.
        /// </summary>
        public Type ArgumentScaffoldType { get; private set; }

        /// <summary>
        /// The command line arguments that are global to this definition.
        /// </summary>
        public List<CommandLineArgument> Arguments { get; private set; }

        /// <summary>
        /// Global hooks that can execute all hook override methods except those that target a particular argument.
        /// </summary>
        public ReadOnlyCollection<ArgHook> Hooks
        {
            get
            {
                return Metadata.Metas<ArgHook>().AsReadOnly();
            }
        }
        
        /// <summary>
        /// Actions that are defined for this definition.  If you have at least one action then the end user must specify the action as the first argument to your program.
        /// </summary>
        public List<CommandLineAction> Actions { get; private set; }

        /// <summary>
        /// Arbitrary metadata that has been added to the definition
        /// </summary>
        public List<ICommandLineArgumentsDefinitionMetadata> Metadata { get; private set; }

        /// <summary>
        /// Examples that show users how to use your program.
        /// </summary>
        public ReadOnlyCollection<ArgExample> Examples
        {
            get
            {
                return Metadata.Metas<ArgExample>().AsReadOnly();
            }
        }

        /// <summary>
        /// Determines how end user errors should be handled by the parser.  By default all exceptions flow through to your program.
        /// </summary>
        public ArgExceptionBehavior ExceptionBehavior { get; set; }

        /// <summary>
        /// If your definition declares actions and has been successfully parsed then this property will be populated
        /// with the action that the end user specified.
        /// </summary>
        public CommandLineAction SpecifiedAction
        {
            get
            {
                var ret = Actions.Where(a => a.IsSpecifiedAction).SingleOrDefault();
                return ret;
            }
        }

        /// <summary>
        /// Creates an empty command line arguments definition.
        /// </summary>
        public CommandLineArgumentsDefinition()
        {
            PropertyInitializer.InitializeFields(this, 1);
            ExceptionBehavior = new ArgExceptionBehavior();
        }

        /// <summary>
        /// Creates a command line arguments definition and infers things like Arguments, Actions, etc. from the type's metadata.
        /// </summary>
        /// <param name="t">The argument scaffold type used to infer the definition</param>
        public CommandLineArgumentsDefinition (Type t) : this()
        {
            ArgumentScaffoldType = t;
            ExceptionBehavior = t.HasAttr<ArgExceptionBehavior>() ? t.Attr<ArgExceptionBehavior>() : new ArgExceptionBehavior();
            Arguments.AddRange(FindCommandLineArguments(t));
            Actions.AddRange(FindCommandLineActions(t));
            Metadata.AddRange(t.Attrs<IArgMetadata>().AssertAreAllInstanceOf<ICommandLineArgumentsDefinitionMetadata>());
        }

        /// <summary>
        /// Gets a basic string representation of the definition.
        /// </summary>
        /// <returns>a basic string representation of the definition</returns>
        public override string ToString()
        {
            var ret = "";

            if (ArgumentScaffoldType != null) ret += ArgumentScaffoldType.Name;
            ret += "(Arguments=" + Arguments.Count + ")";
            ret += "(Actions=" + Actions.Count + ")";
            ret += "(Hooks=" + Hooks.Count() + ")";

            return ret;
        }




        internal void SetPropertyValues(object o)
        {
            foreach (var argument in Arguments)
            {
                var property = argument.Source as PropertyInfo;
                if (property == null) return;
                property.SetValue(o, argument.RevivedValue, null);
            }
        }

        internal void Validate()
        {
            ValidateArguments(Arguments);

            foreach (var action in Actions)
            {
                if (action.Aliases.Count == 0) throw new InvalidArgDefinitionException("One of your actions has no aliases");
                ValidateArguments(Arguments.Union(action.Arguments));
                if (action.ActionMethod == null) throw new InvalidArgDefinitionException("The action '"+action.DefaultAlias+"' has no ActionMethod defined");
            }
        }

        private List<CommandLineAction> FindCommandLineActions(Type t)
        {
            var knownAliases = new List<string>();
            foreach (var argument in Arguments) knownAliases.AddRange(argument.Aliases);

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

            var actions = (from p in t.GetProperties(flags)
                           where  CommandLineAction.IsActionImplementation(p)
                           select CommandLineAction.Create(p, knownAliases)).ToList();

            if (t.HasAttr<ArgActionType>())
            {
                t = t.Attr<ArgActionType>().ActionType;
                flags = BindingFlags.Static | BindingFlags.Public;
            }

            foreach (var action in t.GetMethods(flags).Where(m => CommandLineAction.IsActionImplementation(m)).Select(m => CommandLineAction.Create(m, knownAliases.ToList())))
            {
                var matchingPropertyBasedAction = actions.Where(a => a.Aliases.First() == action.Aliases.First()).SingleOrDefault();
                if (matchingPropertyBasedAction != null) continue;
                actions.Add(action);
            }

            return actions;
        }

        private static List<CommandLineArgument> FindCommandLineArguments(Type t)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

            var knownAliases = new List<string>();

            foreach (var prop in t.GetProperties(flags))
            {
                // This makes sure that explicit aliases get put into the known aliases before any auto generated aliases
                knownAliases.AddRange(prop.Attrs<ArgShortcut>().Select(s => s.Shortcut));
            }

            var ret = from p in t.GetProperties(flags) 
                      where  CommandLineArgument.IsArgument(p) 
                      select CommandLineArgument.Create(p, knownAliases);
            return ret.ToList();
        }

        private static void ValidateArguments(IEnumerable<CommandLineArgument> arguments)
        {
            List<string> knownAliases = new List<string>();

            foreach (var argument in arguments)
            {
                foreach (var alias in argument.Aliases)
                {
                    if (knownAliases.Contains(alias, new CaseAwareStringComparer(argument.IgnoreCase))) throw new InvalidArgDefinitionException("Duplicate alias '" + alias + "' on argument '" + argument.Aliases.First() + "'");
                    knownAliases.Add(alias);
                }
            }

            foreach (var argument in arguments)
            {
                if (argument.ArgumentType == null)
                {
                    throw new InvalidArgDefinitionException("Argument '" + argument.DefaultAlias + "' has a null ArgumentType");
                }

                if (ArgRevivers.CanRevive(argument.ArgumentType) == false)
                {
                    throw new InvalidArgDefinitionException("There is no reviver for type '" + argument.ArgumentType.Name + '"');
                }

                if (argument.ArgumentType.IsEnum)
                {
                    argument.ArgumentType.ValidateNoDuplicateEnumShortcuts(argument.IgnoreCase);
                }

                try
                {
                    foreach (var property in argument.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        // Getting each property will result in all AttrOverrides being validated
                        var val = property.GetValue(argument, null);
                    }
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException is InvalidArgDefinitionException)
                    {
                        throw ex.InnerException;
                    }
                }
            }
        }
    }
}
