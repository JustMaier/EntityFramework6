﻿namespace System.Data.Entity.Core.Common.Internal.Materialization
{
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Data.Entity.Core.Metadata.Edm;
    using System.Data.Entity.Core.Objects;
    using System.Data.Entity.Internal;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Moq;
    using Xunit;

    public class ShaperTests
    {
        [Fact]
        public void GetEnumerator_returns_SimpleEnumerator_for_simple_CoordinatorFactory_sync()
        {
            GetEnumerator_returns_SimpleEnumerator_for_simple_CoordinatorFactory(SetupReadSync, e => e.ToList());
        }

        [Fact]
        public void GetEnumerator_returns_SimpleEnumerator_for_simple_CoordinatorFactory_async()
        {
            GetEnumerator_returns_SimpleEnumerator_for_simple_CoordinatorFactory(SetupReadAsync, e => e.ToListAsync().Result);
        }

        private void GetEnumerator_returns_SimpleEnumerator_for_simple_CoordinatorFactory(Action<Mock<DbDataReader>, IEnumerator<object>> setupRead,
            Func<IDbEnumerator<object>, List<object>> toList)
        {
            var sourceEnumerable = new[] { new object[] { 1 }, new object[] { 2 } };
            var underlyingEnumerator = ((IEnumerable<object[]>)sourceEnumerable).GetEnumerator();

            var dbDataReaderMock = new Mock<DbDataReader>();
            setupRead(dbDataReaderMock, underlyingEnumerator);
            dbDataReaderMock.Setup(m => m.GetValue(It.IsAny<int>())).Returns((int ordinal) => underlyingEnumerator.Current[ordinal]);

            var coordinatorFactory = MockHelper.CreateCoordinatorFactory<object>(shaper => shaper.Reader.GetValue(0));

            var shaperMock = new Mock<Shaper<object>>(dbDataReaderMock.Object, /*context*/ null, /*workspace*/ null,
                MergeOption.AppendOnly, /*stateCount*/ 1, coordinatorFactory, /*checkPermissions*/ null,
                /*readerOwned*/ false) { CallBase = true };

            var actualEnumerator = shaperMock.Object.GetEnumerator();

            Assert.Equal(sourceEnumerable.SelectMany(e => e).ToList(), toList(actualEnumerator));
        }

        [Fact]
        public void GetEnumerator_returns_ObjectQueryNestedEnumerator_for_nested_coordinatorFactories_sync()
        {
            GetEnumerator_returns_ObjectQueryNestedEnumerator_for_nested_coordinatorFactories(SetupReadSync, e => e.ToList());
        }

        [Fact]
        public void GetEnumerator_returns_ObjectQueryNestedEnumerator_for_nested_coordinatorFactories_async()
        {
            GetEnumerator_returns_ObjectQueryNestedEnumerator_for_nested_coordinatorFactories(SetupReadAsync, e => e.ToListAsync().Result);
        }

        private void GetEnumerator_returns_ObjectQueryNestedEnumerator_for_nested_coordinatorFactories(Action<Mock<DbDataReader>, IEnumerator<object>> setupRead,
            Func<IDbEnumerator<object>, List<object>> toList)
        {
            var sourceEnumerable = new[]
                                       {
                                           new object[] { 1, "A", null },
                                           new object[] { 2, null, "X" },
                                           new object[] { 3, "B", "Z" }, // Should stop reading at "B", since the coordinators are at the same depth
                                           new object[] { 4, "C", null },
                                           new object[] { 4, "D", null } // 4 shouldn't be added as it's repeated
                                       };

            var underlyingEnumerator = ((IEnumerable<object[]>)sourceEnumerable).GetEnumerator();

            var dbDataReaderMock = new Mock<DbDataReader>();
            setupRead(dbDataReaderMock, underlyingEnumerator);
            dbDataReaderMock.Setup(m => m.GetValue(It.IsAny<int>())).Returns((int ordinal) => underlyingEnumerator.Current[ordinal]);

            var actualValuesFromNestedCoordinatorOne = new List<string>();
            var nestedCoordinatorFactoryOne = CreateCoordinatorFactory<string, string>(
                depth: 1,
                stateSlot: 1,
                ordinal: 1,
                nestedCoordinators: new CoordinatorFactory[0],
                producedValues: actualValuesFromNestedCoordinatorOne);

            var actualValuesFromNestedCoordinatorTwo = new List<string>();
            var nestedCoordinatorFactoryTwo = CreateCoordinatorFactory<string, string>(
                depth: 1,
                stateSlot: 2,
                ordinal: 2,
                nestedCoordinators: new CoordinatorFactory[0],
                producedValues: actualValuesFromNestedCoordinatorTwo);

            var actualValuesFromRootCoordinator = new List<object>();
            var rootCoordinatorFactory = CreateCoordinatorFactory<int, object>(
                depth: 0,
                stateSlot: 0,
                ordinal: 0,
                nestedCoordinators: new[] { nestedCoordinatorFactoryOne, nestedCoordinatorFactoryTwo },
                producedValues: actualValuesFromRootCoordinator);

            var shaperMock = new Mock<Shaper<object>>(dbDataReaderMock.Object, /*context*/ null, /*workspace*/ null,
                MergeOption.AppendOnly, /*stateCount*/ 3, rootCoordinatorFactory, /*checkPermissions*/ null,
                /*readerOwned*/ false) { CallBase = true };

            var actualEnumerator = shaperMock.Object.GetEnumerator();

            Assert.Equal(new object[] { 1, 2, 3, 4 }.ToList(), toList(actualEnumerator));
            Assert.Equal(new object[] { 1, 2, 3, 4 }.ToList(), actualValuesFromRootCoordinator);
            Assert.Equal(new[] { "A", "B", "C", "D" }.ToList(), actualValuesFromNestedCoordinatorOne);
            Assert.Equal(new[] { "X" }.ToList(), actualValuesFromNestedCoordinatorTwo);
        }

        [Fact]
        public void GetEnumerator_returns_RecordStateEnumerator_for_nested_coordinatorFactories_of_RecordState_sync()
        {
            GetEnumerator_returns_RecordStateEnumerator_for_nested_coordinatorFactories_of_RecordState(SetupReadSync,
                e =>
                {
                    var actualValues = new List<object>();
                    while (e.MoveNext())
                    {
                        actualValues.Add(e.Current.PendingColumnValues[0]);
                    }
                    return actualValues;
                });
        }

        [Fact]
        public void GetEnumerator_returns_RecordStateEnumerator_for_nested_coordinatorFactories_of_RecordState_async()
        {
            GetEnumerator_returns_RecordStateEnumerator_for_nested_coordinatorFactories_of_RecordState(SetupReadAsync,
                e =>
                {
                    var actualValues = new List<object>();
                    while (e.MoveNextAsync(CancellationToken.None).Result)
                    {
                        actualValues.Add(e.Current.PendingColumnValues[0]);
                    }
                    return actualValues;
                });
        }

        private void GetEnumerator_returns_RecordStateEnumerator_for_nested_coordinatorFactories_of_RecordState(Action<Mock<DbDataReader>, IEnumerator<object>> setupRead,
            Func<IDbEnumerator<RecordState>, List<object>> toList)
        {
            var sourceEnumerable = new[]
                                       {
                                           new object[] { 1, "A", null },
                                           new object[] { 2, null, "X" },
                                           new object[] { 3, "B", "Z" }, // Should stop reading at "B", since the coordinators are at the same depth
                                           new object[] { 4, "C", null },
                                           new object[] { 4, "D", null } // 4 shouldn't be added as it's repeated
                                       };

            var underlyingEnumerator = ((IEnumerable<object[]>)sourceEnumerable).GetEnumerator();

            var dbDataReaderMock = new Mock<DbDataReader>();
            setupRead(dbDataReaderMock, underlyingEnumerator);
            dbDataReaderMock.Setup(m => m.GetValue(It.IsAny<int>())).Returns((int ordinal) => underlyingEnumerator.Current[ordinal]);
            dbDataReaderMock.Setup(m => m.IsDBNull(It.IsAny<int>())).Returns((int ordinal) => underlyingEnumerator.Current[ordinal] == null);

            var nestedCoordinatorFactoryOne = CreateCoordinatorFactory<string, RecordState>(
                depth: 1,
                stateSlot: 2,
                ordinal: 1,
                nestedCoordinators: new CoordinatorFactory[0],
                producedValues: null);

            var nestedCoordinatorFactoryTwo = CreateCoordinatorFactory<string, RecordState>(
                depth: 1,
                stateSlot: 4,
                ordinal: 2,
                nestedCoordinators: new CoordinatorFactory[0],
                producedValues: null);

            var rootCoordinatorFactory = CreateCoordinatorFactory<int, RecordState>(
                depth: 0,
                stateSlot: 0,
                ordinal: 0,
                nestedCoordinators: new[] { nestedCoordinatorFactoryOne, nestedCoordinatorFactoryTwo },
                producedValues: null);

            var shaperMock = new Mock<Shaper<RecordState>>(dbDataReaderMock.Object, /*context*/ null, /*workspace*/ null,
                MergeOption.AppendOnly, /*stateCount*/ 6, rootCoordinatorFactory, /*checkPermissions*/ null,
                /*readerOwned*/ false) { CallBase = true };

            Assert.Equal(new object[] { 1, "A", 2, "X", 3, "B", 4, "C", "D" }.ToList(), toList(shaperMock.Object.RootEnumerator));
        }

        private CoordinatorFactory<TResult> CreateCoordinatorFactory<TKey, TResult>(
            int depth, int stateSlot, int ordinal, CoordinatorFactory[] nestedCoordinators, List<TResult> producedValues)
            where TResult : class
        {
            var recordStateFactories = new RecordStateFactory[0];
            Expression<Func<Shaper, TResult>> element;
            if (typeof(TResult) == typeof(RecordState))
            {
                element = shaper => (shaper.Reader.IsDBNull(ordinal)
                                        ? ((RecordState)shaper.State[stateSlot + 1]).SetNullRecord()
                                        : ((RecordState)shaper.State[stateSlot + 1]).GatherData(shaper)) as TResult;

                recordStateFactories = new[] { new RecordStateFactory(
                    stateSlotNumber: stateSlot + 1,
                    columnCount: 1,
                    nestedRecordStateFactories: new RecordStateFactory[0],
                    dataRecordInfo: null,
                    gatherData: shaper => shaper.SetColumnValue(stateSlot + 1, 0, shaper.Reader.GetValue(ordinal)),
                    propertyNames: new string[0],
                    typeUsages: new TypeUsage[0],
                    isColumnNested: new[] { false })};
            }
            else
            {
                element = shaper => producedValues.AddAndReturn((TResult)shaper.Reader.GetValue(ordinal)) as TResult;
            }

            var keysStore = new HashSet<TKey>();
            return new CoordinatorFactory<TResult>(
                depth: depth,
                stateSlot: stateSlot,
                hasData: shaper => shaper.Reader.GetValue(ordinal) != null,
                setKeys: shaper => keysStore.Add((TKey)shaper.Reader.GetValue(ordinal)),
                checkKeys: shaper => keysStore.Contains((TKey)shaper.Reader.GetValue(ordinal)),
                nestedCoordinators: nestedCoordinators,
                element: element,
                wrappedElement: null,
                elementWithErrorHandling: element,
                initializeCollection: null,
                recordStateFactories: recordStateFactories);
        }

        private void SetupReadSync(Mock<DbDataReader> dbDataReaderMock, IEnumerator<object> underlyingEnumerator)
        {
            dbDataReaderMock.Setup(m => m.Read()).Returns(underlyingEnumerator.MoveNext);
        }

        private void SetupReadAsync(Mock<DbDataReader> dbDataReaderMock, IEnumerator<object> underlyingEnumerator)
        {
            dbDataReaderMock.Setup(m => m.ReadAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) => Task.FromResult(underlyingEnumerator.MoveNext()));
        }

    }
}