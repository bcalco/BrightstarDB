﻿using System;
using System.Collections.Generic;
using System.IO;
using BrightstarDB.Profiling;

namespace BrightstarDB.Storage.Persistence
{
    internal class AppendOnlyFilePageStore : IPageStore
    {
        private readonly string _path;
        private readonly Stream _stream;
        private readonly int _pageSize;
        private readonly int _bitShift;
        private ulong _nextPageId;
        private readonly bool _readonly;
        private bool _disposed;
        private ulong _newPageOffset;
        private readonly List<IPage> _newPages;
        private readonly IPersistenceManager _peristenceManager;
        private BackgroundPageWriter _backgroundPageWriter;
        
        public AppendOnlyFilePageStore(IPersistenceManager persistenceManager, string filePath, int pageSize, bool readOnly, bool disableBackgroundWrites)
        {
            _peristenceManager = persistenceManager;
            _path = filePath;

            if ((_pageSize % 4096) != 0)
            {
                throw new ArgumentException("Page size must be a multiple of 4096 bytes");
            }
            _pageSize = pageSize;
            _bitShift = (int)Math.Log(_pageSize, 2.0);

            if (!_peristenceManager.FileExists(filePath) && !readOnly)
            {
                // Create an empty file that we can write to later
                _peristenceManager.CreateFile(filePath);
            }
            _stream = _peristenceManager.GetInputStream(_path);
            _nextPageId = ((ulong)_stream.Length >> _bitShift) + 1;
            if (!readOnly)
            {
                _newPages = new List<IPage>(512);
                _newPageOffset = _nextPageId;
            }
            _pageSize = pageSize;
            _readonly = readOnly;

            if (!readOnly && !disableBackgroundWrites)
            {
                _backgroundPageWriter =
                    new BackgroundPageWriter(persistenceManager.GetOutputStream(filePath, FileMode.Open));
            }

            PageCache.Instance.BeforeEvict += BeforePageCacheEvict;
        }

        /// <summary>
        /// Handles notification of page eviction from the page cache
        /// </summary>
        /// <param name="sender">The page cache performing the eviction</param>
        /// <param name="args">The evication event arguments</param>
        /// <remarks>When the eviction event is for a writeable page, this handler 
        /// ensures that the page is queued with the background page writer. If there
        /// is no background page writer because the page store was created with the 
        /// disableBackgroundWriter option, then this method cancels an eviction
        /// for a writeable page.</remarks>
        private void BeforePageCacheEvict(object sender, EvictionEventArgs args)
        {
            if (args.Partition.Equals(_path))
            {
                // Evicting a page from this store
                if (args.PageId > _newPageOffset)
                {
                    // Evicting a writeable page - add the page to the background write queue to ensure it gets written out.
                    // Note: the background page writer will hold on to the page data until it is written
                    if (_backgroundPageWriter == null)
                    {
                        // Do not evict this page
                        args.CancelEviction = true;
                    }
                    else
                    {
                        // Queue the page with the background page writer
                        var pageToEvict = _newPages[(int) (args.PageId - _newPageOffset)];
                        _backgroundPageWriter.QueueWrite(pageToEvict, 0ul);
                            // Passing 0 for the transaction id is OK because it is not used for writing append-only pages
                    }
                }
            }
        }

        #region Implementation of IPageStore

        public IPage Retrieve(ulong pageId, BrightstarProfiler profiler)
        {
            using (profiler.Step("PageStore.Retrieve"))
            {
                if (!_readonly && pageId >= _newPageOffset)
                {
                    var newPage = _newPages[(int) (pageId - _newPageOffset)];
                    return newPage;
                }
                var page = PageCache.Instance.Lookup(_path, pageId) as FilePage;
                if (page != null)
                {
                    profiler.Incr("PageCache Hit");
                    return page;
                }
                using (profiler.Step("Load Page"))
                {
                    profiler.Incr("PageCache Miss");
                    using (profiler.Step("Create FilePage"))
                    {
                        // Lock on stream to prevent attempts to concurrently load a page
                        lock (_stream)
                        {
                            page = new FilePage(_stream, pageId, _pageSize);
                        }
                    }
                    using (profiler.Step("Add FilePage To Cache"))
                    {
                        PageCache.Instance.InsertOrUpdate(_path, page);
                    }
                    return page;
                }
            }
        }

        public IPage Create(ulong commitId)
        {
            if (_readonly) throw new InvalidOperationException("Cannot create new pages in readonly page store");
            var dataPage = new FilePage(_nextPageId, _pageSize);
            _newPages.Add(dataPage);
            _nextPageId++;
            return dataPage;
        }

        private IPage Create(ulong txnId, byte[] pageData, int srcOffset = 0, int pageOffset = 0, int len = -1)
        {
            var page = Create(txnId);
            page.SetData(pageData, srcOffset, pageOffset, len);
            return page;
        }

        public void Commit(ulong commitId, BrightstarProfiler profiler)
        {
            using (profiler.Step("PageStore.Commit"))
            {
                if (_backgroundPageWriter != null)
                {
                    foreach (var p in _newPages)
                    {
                        _backgroundPageWriter.QueueWrite(p, commitId);
                    }
                    _backgroundPageWriter.Flush();
                    RestartBackgroundWriter();
                    foreach (var p in _newPages)
                    {
                        PageCache.Instance.InsertOrUpdate(_path, p);
                    }
                }
                else
                {
                    using (var outputStream = _peristenceManager.GetOutputStream(_path, FileMode.Open))
                    {
                        foreach (var p in _newPages)
                        {
                            p.Write(outputStream, commitId);
                            PageCache.Instance.InsertOrUpdate(_path, p);
                        }
                    }
                }
                _newPages.Clear();
                _newPageOffset = _nextPageId;
            }
            /*
            using (var writeStream = _peristenceManager.GetOutputStream(_path, FileMode.Open))
            {
                writeStream.Seek((long) ((_newPageOffset - 1)*(ulong) _pageSize), SeekOrigin.Begin);
                foreach (var p in _newPages)
                {
                    writeStream.Write(p.Data, 0, _pageSize);
                }
                writeStream.Flush();
                _newPages.Clear();
                _newPageOffset = _nextPageId;
            }
             */
        }

        public void Write(ulong commitId, ulong pageId, byte[] data, int srcOffset = 0, int pageOffset = 0, int len = -1, BrightstarProfiler profiler = null)
        {
            if (pageId < _newPageOffset)
            {
                throw new InvalidOperationException("Attempt to write to a fixed page");
            }
            var pageIx = (int) (pageId - _newPageOffset);
            if (pageIx >= _newPages.Count)
            {
                throw new InvalidOperationException("Attempt to write to an unreserved page");
            }
            using (profiler.Step("Write Page"))
            {
                _newPages[pageIx].SetData(data, srcOffset, pageOffset, len);
                if (_backgroundPageWriter != null)
                {
                    _backgroundPageWriter.QueueWrite(_newPages[pageIx], commitId);
                }
            }
        }

        /// <summary>
        /// Returns a boolean flag indicating if the page with the specified page ID is writeable
        /// </summary>
        /// <param name="page">The page to test</param>
        /// <returns>True if the page is writeable, false otherwise</returns>
        /// <remarks>In an append-only store, only pages created since the last commit are writeable. In a binary-page store, all pages are always writeable. 
        /// Client code should use this method to determine if an update to a page can be done by a call to Write() or if a new page needs to be created using Create()</remarks>
        public bool IsWriteable(IPage page)
        {
            return page.Id >= _newPageOffset;
        }

        public IPage GetWriteablePage(ulong txnId, IPage page)
        {
            if (IsWriteable(page)) return page;
            return Create(txnId, page.Data);
        }

        /// <summary>
        /// Get the size (in bytes) of each data page
        /// </summary>
        public int PageSize
        {
            get { return _pageSize; }
        }

        /// <summary>
        /// Get the flag that indicates if the store can be read from
        /// </summary>
        public bool CanRead
        {
            get { return true; }
        }

        /// <summary>
        /// Get the flag that indicates if the store can be written to
        /// </summary>
        public bool CanWrite
        {
            get { return !_readonly; }
        }

        /// <summary>
        /// Close the store, releasing any resources (such as file handles) it may be using
        /// </summary>
        public void Close()
        {
            if (_stream != null)
            {
                _stream.Close();
            }
            if (_backgroundPageWriter != null)
            {
                _backgroundPageWriter.Shutdown();
                _backgroundPageWriter.Dispose();
                _backgroundPageWriter = null;
            }
        }

        #endregion

        #region Implementation of IDisposable

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Close();
                    if (_backgroundPageWriter != null)
                    {
                        _backgroundPageWriter.Dispose();
                        _backgroundPageWriter = null;
                    }
                }
                _disposed = true;
            }
        }

        ~AppendOnlyFilePageStore()
        {
            Dispose(false);
        }

        private void RestartBackgroundWriter()
        {
            lock (this)
            {
                _backgroundPageWriter.Shutdown();
                _backgroundPageWriter.Dispose();
                _backgroundPageWriter =
                    new BackgroundPageWriter(_peristenceManager.GetOutputStream(_path, FileMode.Open));
            }
        }
    }
}
