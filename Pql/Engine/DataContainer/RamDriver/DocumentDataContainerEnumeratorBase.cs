﻿using System;
using System.Collections.Generic;
using Pql.ClientDriver.Protocol;
using Pql.Engine.Interfaces.Internal;
using Pql.Engine.Interfaces.Services;

namespace Pql.Engine.DataContainer.RamDriver
{
    internal abstract class DocumentDataContainerEnumeratorBase : IDriverDataEnumerator
    {
        protected bool HaveData;
        protected int Position;
        protected readonly int UntrimmedCount;
        protected readonly DriverRowData RowData;
        protected readonly DocumentDataContainer DataContainer;
        protected readonly int[] RowDataOrdinalToColumnStoreIndex;
        protected readonly IReadOnlyList<FieldMetadata> Fields;
        protected readonly int CountOfMainFields;

        public void Dispose()
        {
            DataContainer.StructureLock.ExitReadLock();
        }

        public abstract bool MoveNext();

        public virtual void FetchAdditionalFields()
        {
            // get internal entity id
            DataContainer.DocumentKeys.GetAt(Position, RowData.InternalEntityId);

            // get other columns
            for (var ordinal = CountOfMainFields; ordinal < RowDataOrdinalToColumnStoreIndex.Length; ordinal++)
            {
                var columnStore = DataContainer.ColumnStores[RowDataOrdinalToColumnStoreIndex[ordinal]];
                if (columnStore.NotNulls.SafeGet(Position))
                {
                    BitVector.Set(RowData.NotNulls, ordinal);
                    var indexInArray = RowData.FieldArrayIndexes[ordinal];
                    columnStore.AssignToDriverRow(Position, RowData, indexInArray);
                }
                else
                {
                    BitVector.Clear(RowData.NotNulls, ordinal);
                }
            }
        }

        protected void ReadRow()
        {
            // by default, fetch subset of primary fields only
            // everything else is fetched by FetchAdditionalFields
            for (var ordinal = 0; ordinal < CountOfMainFields; ordinal++)
            {
                var columnStore = DataContainer.ColumnStores[RowDataOrdinalToColumnStoreIndex[ordinal]];
                if (columnStore.NotNulls.SafeGet(Position))
                {
                    BitVector.Set(RowData.NotNulls, ordinal);
                    var indexInArray = RowData.FieldArrayIndexes[ordinal];
                    columnStore.AssignToDriverRow(Position, RowData, indexInArray);
                }
                else
                {
                    BitVector.Clear(RowData.NotNulls, ordinal);
                }
            }
        }

        public DriverRowData Current { get { throw new NotSupportedException(); } }

        public void FetchInternalEntityIdIntoChangeBuffer(DriverChangeBuffer changeBuffer,
            RequestExecutionContext context)
        {
            changeBuffer.InternalEntityId = context.DriverOutputBuffer.InternalEntityId;
        }

        protected DocumentDataContainerEnumeratorBase(
            int untrimmedCount, 
            DriverRowData rowData, 
            DocumentDataContainer dataContainer, 
            IReadOnlyList<FieldMetadata> fields,
            int countOfMainFields)
        {
            if (untrimmedCount < 0)
            {
                throw new ArgumentOutOfRangeException("untrimmedCount", untrimmedCount, "Untrimmed count cannot be negative");
            }

            if (rowData == null)
            {
                throw new ArgumentNullException("rowData");
            }

            if (dataContainer == null)
            {
                throw new ArgumentNullException("dataContainer");
            }

            if (fields == null)
            {
                throw new ArgumentNullException("fields");
            }

            if (CountOfMainFields < 0 || CountOfMainFields > fields.Count)
            {
                throw new ArgumentOutOfRangeException("countOfMainFields", countOfMainFields, "Invalid number of first-priority fetching fields");
            }

            Position = -1;
            UntrimmedCount = untrimmedCount;
            RowData = rowData;
            DataContainer = dataContainer;
            Fields = fields;
            CountOfMainFields = countOfMainFields;

            RowDataOrdinalToColumnStoreIndex = new int[RowData.FieldTypes.Length];

            // ancestors must also invoke ReadStructureAndTakeLocks in their constructor
        }

        /// <summary>
        /// Must be invoked from constructor of ancestors when all other checks are complete.
        /// </summary>
        protected void ReadStructureAndTakeLocks()
        {
            for (var ordinal = 0; ordinal < RowDataOrdinalToColumnStoreIndex.Length; ordinal++)
            {
                var colStoreIndex = RequireColumnStoreIndex(Fields[ordinal].FieldId);
                DataContainer.ColumnStores[colStoreIndex].WaitLoadingCompleted();
                RowDataOrdinalToColumnStoreIndex[ordinal] = colStoreIndex;
            }

            DataContainer.StructureLock.EnterReadLock();
        }

        private int RequireColumnStoreIndex(int fieldId)
        {
            for (var i = 0; i < DataContainer.DocDesc.Fields.Length; i++)
            {
                if (DataContainer.DocDesc.Fields[i] == fieldId)
                {
                    return i;
                }
            }

            throw new KeyNotFoundException("Field with this id does not have a column store: " + fieldId);
        }
    }
}