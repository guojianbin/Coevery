﻿using System;
using System.Linq;
using Coevery.Projections.FieldTypeEditors;
using Coevery.Projections.Models;
using Coevery.Projections.FilterEditors.Forms;
using Coevery.ContentManagement;
using Coevery.Environment.Extensions;
using Coevery.Localization;

namespace Coevery.Projections.FieldTypeEditors {
    [CoeverySuppressDependency("Coevery.Projections.FieldTypeEditors.IntegerFieldTypeEditor")]
    public class IntegerFieldTypeEditor : IFieldTypeEditor {
        public Localizer T { get; set; }

        public IntegerFieldTypeEditor() {
            T = NullLocalizer.Instance;
        }

        public bool CanHandle(Type storageType) {
            return new[] {
                typeof(Byte), 
                typeof(SByte), 
                typeof(Int16), 
                typeof(Int32), 
                typeof(Int64), 
                typeof(UInt16), 
                typeof(UInt32), 
                typeof(UInt64), 
            }.Contains(storageType);
        }

        public string FormName {
            get { return NumericFilterForm.FormName; }
        }

        public Action<IHqlExpressionFactory> GetFilterPredicate(dynamic formState) {
            return NumericFilterForm.GetFilterPredicate(formState, "Value");
        }

        public LocalizedString DisplayFilter(string fieldName, string storageName, dynamic formState) {
            return NumericFilterForm.DisplayFilter(fieldName + " " + storageName, formState, T);
        }

        public Action<IAliasFactory> GetFilterRelationship(string aliasName) {
            return x => x.ContentPartRecord<FieldIndexPartRecord>().Property("IntegerFieldIndexRecords", aliasName);
        }
    }
}