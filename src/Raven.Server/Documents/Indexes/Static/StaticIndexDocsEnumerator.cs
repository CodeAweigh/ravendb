﻿using System.Collections;
using System.Collections.Generic;
using Raven.Server.Documents.Transformers;

namespace Raven.Server.Documents.Indexes.Static
{
    public class StaticIndexDocsEnumerator : IIndexedDocumentsEnumerator
    {
        private readonly EnumerationType _enumerationType;
        private readonly IEnumerable _resultsOfCurrentDocument;
        private readonly IEnumerator<Document> _docsEnumerator;

        public StaticIndexDocsEnumerator(IEnumerable<Document> docs, IndexingFunc func, string collection, EnumerationType enumerationType)
        {
            _enumerationType = enumerationType;
            _docsEnumerator = docs.GetEnumerator();
            _resultsOfCurrentDocument = func(new DynamicIteratonOfCurrentDocumentWrapper(this));

            switch (enumerationType)
            {
                case EnumerationType.Index:
                    CurrentIndexingScope.Current.SourceCollection = collection;
                    break;
                case EnumerationType.Transformer:
                    break;
            }
        }

        public bool MoveNext(out IEnumerable resultsOfCurrentDocument)
        {
            Current?.Data.Dispose();

            if (_docsEnumerator.MoveNext() == false)
            {
                Current = null;
                resultsOfCurrentDocument = null;

                return false;
            }

            Current = _docsEnumerator.Current;
            resultsOfCurrentDocument = _resultsOfCurrentDocument;

            return true;
        }

        public Document Current { get; private set; }

        public void Dispose()
        {
            _docsEnumerator.Dispose();
            Current?.Data?.Dispose();
        }

        public enum EnumerationType
        {
            Index,
            Transformer
        }

        private class DynamicIteratonOfCurrentDocumentWrapper : IEnumerable<DynamicBlittableJson>
        {
            private readonly StaticIndexDocsEnumerator _indexingEnumerator;
            private Enumerator _enumerator;

            public DynamicIteratonOfCurrentDocumentWrapper(StaticIndexDocsEnumerator indexingEnumerator)
            {
                _indexingEnumerator = indexingEnumerator;
            }

            public IEnumerator<DynamicBlittableJson> GetEnumerator()
            {
                return _enumerator ?? (_enumerator = new Enumerator(_indexingEnumerator));
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            private class Enumerator : IEnumerator<DynamicBlittableJson>
            {
                private DynamicBlittableJson _dynamicDocument;
                private readonly StaticIndexDocsEnumerator _inner;
                private Document _seen;

                public Enumerator(StaticIndexDocsEnumerator indexingEnumerator)
                {
                    _inner = indexingEnumerator;
                }

                public bool MoveNext()
                {
                    if (_seen == _inner.Current) // already iterated
                        return false;

                    _seen = _inner.Current;

                    if (_dynamicDocument == null)
                        _dynamicDocument = new DynamicBlittableJson(_seen);
                    else
                        _dynamicDocument.Set(_seen);

                    Current = _dynamicDocument;

                    switch (_inner._enumerationType)
                    {
                        case EnumerationType.Index:
                            CurrentIndexingScope.Current.Source = _dynamicDocument;
                            break;
                        case EnumerationType.Transformer:
                            CurrentTransformationScope.Current.Source = _dynamicDocument;
                            break;
                    }

                    return true;
                }

                public void Reset()
                {
                    throw new System.NotImplementedException();
                }

                public DynamicBlittableJson Current { get; private set; }

                object IEnumerator.Current => Current;

                public void Dispose()
                {
                }
            }
        }
    }
}