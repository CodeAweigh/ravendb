//-----------------------------------------------------------------------
// <copyright file="ReadVetoException.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Runtime.Serialization;

namespace Raven.Client.Exceptions
{
	/// <summary>
	/// This exception is raised whenever a trigger vetoes the read by the session
	/// </summary>
#if !SILVERLIGHT && !NETFX_CORE
	[Serializable]
#endif
	public class ReadVetoException : Exception
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="ReadVetoException"/> class.
		/// </summary>
		public ReadVetoException()
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ReadVetoException"/> class.
		/// </summary>
		/// <param name="message">The message.</param>
		public ReadVetoException(string message) : base(message)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ReadVetoException"/> class.
		/// </summary>
		/// <param name="message">The message.</param>
		/// <param name="inner">The inner.</param>
		public ReadVetoException(string message, Exception inner) : base(message, inner)
		{
		}
#if !SILVERLIGHT && !NETFX_CORE
		/// <summary>
		/// Initializes a new instance of the <see cref="ReadVetoException"/> class.
		/// </summary>
		/// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
		/// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext"/> that contains contextual information about the source or destination.</param>
		/// <exception cref="T:System.ArgumentNullException">The <paramref name="info"/> parameter is null. </exception>
		/// <exception cref="T:System.Runtime.Serialization.SerializationException">The class name is null or <see cref="P:System.Exception.HResult"/> is zero (0). </exception>
		protected ReadVetoException(
			SerializationInfo info,
			StreamingContext context) : base(info, context)
		{
		}
#endif
	}
}
