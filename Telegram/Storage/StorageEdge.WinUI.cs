//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The shim BannedSymbols.WinUI.txt sends every other caller to.
#pragma warning disable RS0030

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;

namespace Telegram.Storage
{
    // Where a Windows.Storage object crosses into the app or out of it: pickers, drag-drop and
    // paste, activation, share, Launcher, recording. Extensions rather than statics on the types,
    // because on UWP FileItem is StorageFile and has no such statics.
    public static class StorageEdge
    {
        public static FileItem ToFileItem(this StorageFile file)
        {
            return file == null ? null : new FileItem(file);
        }

        public static FolderItem ToFolderItem(this StorageFolder folder)
        {
            return folder == null ? null : new FolderItem(folder);
        }

        // Null for an item that is neither a file nor a folder, which nothing has produced so far.
        public static IFileSystemItem ToFileSystemItem(this IStorageItem item)
        {
            return item switch
            {
                StorageFile file => new FileItem(file),
                StorageFolder folder => new FolderItem(folder),
                _ => null
            };
        }

        /// <summary>
        /// The operation's files, copied on the thread that completed it. The projection fetches
        /// the result in the completion callback, which for the file picker runs on an STA thread
        /// of its own, and its list is not agile: CsWinRT 3 does not marshal it, so read on the UI
        /// thread after an await, every call fails with RPC_E_WRONG_THREAD. The files in it are agile.
        /// </summary>
        public static Task<IReadOnlyList<FileItem>> ToFileItemsAsync(this IAsyncOperation<IReadOnlyList<StorageFile>> operation)
        {
            return new ListConversion<StorageFile, FileItem>(operation, ToFileItems).Task;
        }

        /// <inheritdoc cref="ToFileItemsAsync"/>
        public static Task<IReadOnlyList<IFileSystemItem>> ToFileSystemItemsAsync(this IAsyncOperation<IReadOnlyList<IStorageItem>> operation)
        {
            return new ListConversion<IStorageItem, IFileSystemItem>(operation, ToFileSystemItems).Task;
        }

        private sealed class ListConversion<TSource, TResult>
        {
            private readonly TaskCompletionSource<IReadOnlyList<TResult>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Func<IReadOnlyList<TSource>, IReadOnlyList<TResult>> _convert;

            public ListConversion(IAsyncOperation<IReadOnlyList<TSource>> operation, Func<IReadOnlyList<TSource>, IReadOnlyList<TResult>> convert)
            {
                _convert = convert;

                // Assigned after completion, the handler runs at once on this thread.
                operation.Completed = OnCompleted;
            }

            public Task<IReadOnlyList<TResult>> Task => _completion.Task;

            private void OnCompleted(IAsyncOperation<IReadOnlyList<TSource>> operation, AsyncStatus status)
            {
                try
                {
                    switch (status)
                    {
                        case AsyncStatus.Completed:
                            _completion.TrySetResult(_convert(operation.GetResults()));
                            break;
                        case AsyncStatus.Error:
                            _completion.TrySetException(operation.ErrorCode);
                            break;
                        default:
                            _completion.TrySetCanceled();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                }
            }
        }

        // Only on the thread the list was fetched on: see ToFileItemsAsync.
        public static IReadOnlyList<FileItem> ToFileItems(this IReadOnlyList<StorageFile> files)
        {
            var items = new FileItem[files.Count];

            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new FileItem(files[i]);
            }

            return items;
        }

        // Skips what ToFileSystemItem would turn into null, so the result never holds one.
        public static IReadOnlyList<IFileSystemItem> ToFileSystemItems(this IReadOnlyList<IStorageItem> items)
        {
            var result = new List<IFileSystemItem>(items.Count);

            foreach (var item in items)
            {
                if (item.ToFileSystemItem() is IFileSystemItem converted)
                {
                    result.Add(converted);
                }
            }

            return result;
        }

        public static Task<StorageFile> ToStorageFileAsync(this FileItem file)
        {
            return Task.FromResult(file?.Source);
        }

        public static Task<StorageFolder> ToStorageFolderAsync(this FolderItem folder)
        {
            return Task.FromResult(folder?.Source);
        }

        public static Task<IStorageItem> ToStorageItemAsync(this IFileSystemItem item)
        {
            return Task.FromResult<IStorageItem>(item switch
            {
                FileItem file => file.Source,
                FolderItem folder => folder.Source,
                _ => null
            });
        }

        // Call sites written against IAsyncOperation call AsTask() on what is a Task here.
        public static Task<T> AsTask<T>(this Task<T> task)
        {
            return task;
        }

        public static Task AsTask(this Task task)
        {
            return task;
        }
    }
}
