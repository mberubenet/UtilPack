﻿/*
 * Copyright 2018 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UtilPack;
using UtilPack.AsyncEnumeration;
using UtilPack.AsyncEnumeration.LINQ;

namespace UtilPack.AsyncEnumeration.LINQ
{
   internal sealed class SelectManyEnumeratorSync<TSource, TResult> : IAsyncEnumerator<TResult>
   {

      private readonly IAsyncEnumerator<TSource> _source;
      private readonly Func<TSource, IEnumerable<TResult>> _selector;

      private IEnumerator<TResult> _current;

      public SelectManyEnumeratorSync(
         IAsyncEnumerator<TSource> source,
          Func<TSource, IEnumerable<TResult>> selector
         )
      {
         this._source = ArgumentValidator.ValidateNotNull( nameof( source ), source );
         this._selector = ArgumentValidator.ValidateNotNull( nameof( selector ), selector );
      }

      public Task<Boolean> WaitForNextAsync()
      {
         var current = this._current;
         current?.Dispose();
         this._current = null;
         return this._source.WaitForNextAsync();
      }

      public TResult TryGetNext( out Boolean success )
      {
         var current = this._current;
         if ( current == null || !current.MoveNext() )
         {
            current?.Dispose();
            current = null;
            do
            {
               var item = this._source.TryGetNext( out success );
               if ( success )
               {
                  var enumerator = this._selector( item )?.GetEnumerator();
                  if ( enumerator != null )
                  {
                     if ( enumerator.MoveNext() )
                     {
                        this._current = current = enumerator;
                     }
                     else
                     {
                        enumerator.Dispose();
                     }
                  }

               }
            } while ( current == null && success );
            success = success && current != null;
         }
         else
         {
            success = true;
         }

         return current == null ? default : current.Current;
      }

      public Task DisposeAsync()
      {
         return this._source.DisposeAsync();
      }
   }

   internal sealed class SelectManyEnumeratorAsync<TSource, TResult> : IAsyncEnumerator<TResult>
   {
      private const Int32 CALL_WAIT = 0;
      private const Int32 CALL_TRYGET = 1;
      private const Int32 ENDED = 2;

      private readonly IAsyncEnumerator<TSource> _source;
      private readonly Func<TSource, IAsyncEnumerable<TResult>> _selector;

      private IAsyncEnumerator<TResult> _current;
      private Int32 _state;

      public SelectManyEnumeratorAsync(
         IAsyncEnumerator<TSource> source,
         Func<TSource, IAsyncEnumerable<TResult>> selector
         )
      {
         this._source = ArgumentValidator.ValidateNotNull( nameof( source ), source );
         this._selector = ArgumentValidator.ValidateNotNull( nameof( selector ), selector );
      }

      public Task<Boolean> WaitForNextAsync()
      {
         return this._state == ENDED ?
            TaskUtils.False :
            this.PerformWaitForNextAsync();
      }

      public TResult TryGetNext( out Boolean success )
      {
         return this._current.TryGetNext( out success );
      }

      public Task DisposeAsync()
      {
         return this._source.DisposeAsync();
      }

      private async Task<Boolean> PerformWaitForNextAsync()
      {
         var current = this._current;
         Boolean retVal;
         if ( current == null || !( await current.WaitForNextAsync() ) )
         {
            if ( current != null )
            {
               await current.DisposeAsync();
            }
            current = null;

            var state = this._state;
            do
            {
               if ( state == CALL_WAIT )
               {
                  retVal = await this._source.WaitForNextAsync();
                  if ( retVal )
                  {
                     state = CALL_TRYGET;
                  }
               }
               if ( state == CALL_TRYGET )
               {
                  var item = this._source.TryGetNext( out retVal );
                  if ( retVal )
                  {
                     current = this._selector( item )?.GetAsyncEnumerator();
                  }
                  else
                  {
                     state = CALL_WAIT;
                  }
               }
               else
               {
                  state = ENDED;
               }
            } while ( state == CALL_WAIT || ( state == CALL_TRYGET && current == null ) );

            Interlocked.Exchange( ref this._state, state );
            Interlocked.Exchange( ref this._current, current );
         }

         return this._state != ENDED;
      }
   }
}

public static partial class E_UtilPack
{
   /// <summary>
   /// This extension method will return <see cref="IAsyncEnumerable{T}"/> which will flatten the items returned by given selector callback.
   /// </summary>
   /// <typeparam name="T">The type of source items.</typeparam>
   /// <typeparam name="U">The type of target items.</typeparam>
   /// <param name="enumerable">This <see cref="IAsyncEnumerable{T}"/>.</param>
   /// <param name="selector">The callback to transform a single item into enumerable of items of type <typeparamref name="U"/>.</param>
   /// <returns><see cref="IAsyncEnumerable{T}"/> which will return items as flattened asynchronous enumerable.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="IAsyncEnumerable{T}"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="selector"/> is <c>null</c>.</exception>
   /// <seealso cref="System.Linq.Enumerable.SelectMany{TSource, TResult}(IEnumerable{TSource}, Func{TSource, IEnumerable{TResult}})"/>
   public static IAsyncEnumerable<U> SelectMany<T, U>( this IAsyncEnumerable<T> enumerable, Func<T, IEnumerable<U>> selector )
   {
      ArgumentValidator.ValidateNotNullReference( enumerable );
      ArgumentValidator.ValidateNotNull( nameof( selector ), selector );
      return new EnumerableWrapper<U>( () => enumerable.GetAsyncEnumerator().SelectMany( selector ) );
   }

   /// <summary>
   /// This extension method will return <see cref="IAsyncEnumerable{T}"/> which will asynchronously flatten the items returned by given selector callback.
   /// </summary>
   /// <typeparam name="T">The type of source items.</typeparam>
   /// <typeparam name="U">The type of target items.</typeparam>
   /// <param name="enumerable">This <see cref="IAsyncEnumerable{T}"/>.</param>
   /// <param name="asyncSelector">The callback to transform a single item into asynchronous enumerable of items of type <typeparamref name="U"/>.</param>
   /// <returns><see cref="IAsyncEnumerable{T}"/> which will return items as flattened asynchronous enumerable.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="IAsyncEnumerable{T}"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="asyncSelector"/> is <c>null</c>.</exception>
   /// <seealso cref="System.Linq.Enumerable.SelectMany{TSource, TResult}(IEnumerable{TSource}, Func{TSource, IEnumerable{TResult}})"/>
   public static IAsyncEnumerable<U> SelectMany<T, U>( this IAsyncEnumerable<T> enumerable, Func<T, IAsyncEnumerable<U>> asyncSelector )
   {
      ArgumentValidator.ValidateNotNullReference( enumerable );
      ArgumentValidator.ValidateNotNull( nameof( asyncSelector ), asyncSelector );
      return new EnumerableWrapper<U>( () => enumerable.GetAsyncEnumerator().SelectMany( asyncSelector ) );
   }

   /// <summary>
   /// This extension method will return <see cref="IAsyncEnumerator{T}"/> which will flatten the items returned by given selector callback.
   /// </summary>
   /// <typeparam name="T">The type of source items.</typeparam>
   /// <typeparam name="U">The type of target items.</typeparam>
   /// <param name="enumerator">This <see cref="IAsyncEnumerator{T}"/>.</param>
   /// <param name="selector">The callback to transform a single item into enumerable of items of type <typeparamref name="U"/>.</param>
   /// <returns><see cref="IAsyncEnumerator{T}"/> which will return items as flattened asynchronous enumerator.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="IAsyncEnumerator{T}"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="selector"/> is <c>null</c>.</exception>
   /// <seealso cref="System.Linq.Enumerable.SelectMany{TSource, TResult}(IEnumerable{TSource}, Func{TSource, IEnumerable{TResult}})"/>
   public static IAsyncEnumerator<U> SelectMany<T, U>( this IAsyncEnumerator<T> enumerator, Func<T, IEnumerable<U>> selector )
   {
      ArgumentValidator.ValidateNotNullReference( enumerator );
      ArgumentValidator.ValidateNotNull( nameof( selector ), selector );
      return new SelectManyEnumeratorSync<T, U>( enumerator, selector );
   }

   /// <summary>
   /// This extension method will return <see cref="IAsyncEnumerator{T}"/> which will asynchronously flatten the items returned by given selector callback.
   /// </summary>
   /// <typeparam name="T">The type of source items.</typeparam>
   /// <typeparam name="U">The type of target items.</typeparam>
   /// <param name="enumerator">This <see cref="IAsyncEnumerator{T}"/>.</param>
   /// <param name="asyncSelector">The callback to transform a single item into asynchronous enumerable of items of type <typeparamref name="U"/>.</param>
   /// <returns><see cref="IAsyncEnumerator{T}"/> which will return items as flattened asynchronous enumerator.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="IAsyncEnumerator{T}"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="asyncSelector"/> is <c>null</c>.</exception>
   /// <seealso cref="System.Linq.Enumerable.SelectMany{TSource, TResult}(IEnumerable{TSource}, Func{TSource, IEnumerable{TResult}})"/>
   public static IAsyncEnumerator<U> SelectMany<T, U>( this IAsyncEnumerator<T> enumerator, Func<T, IAsyncEnumerable<U>> asyncSelector )
   {
      ArgumentValidator.ValidateNotNullReference( enumerator );
      ArgumentValidator.ValidateNotNull( nameof( asyncSelector ), asyncSelector );
      return new SelectManyEnumeratorAsync<T, U>( enumerator, asyncSelector );
   }
}
