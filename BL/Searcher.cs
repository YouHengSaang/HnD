/*
	This file is part of HnD.
	HnD is (c) 2002-2007 Solutions Design.
    http://www.llblgen.com
	http://www.sd.nl

	HnD is free software; you can redistribute it and/or modify
	it under the terms of version 2 of the GNU General Public License as published by
	the Free Software Foundation.

	HnD is distributed in the hope that it will be useful,
	but WITHOUT ANY WARRANTY; without even the implied warranty of
	MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
	GNU General Public License for more details.

	You should have received a copy of the GNU General Public License
	along with HnD, please see the LICENSE.txt file; if not, write to the Free Software
	Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA
*/
using System;
using System.Collections;
using System.Data;
using System.Text;
using System.Globalization;

using System.Xml;

using SD.LLBLGen.Pro.ORMSupportClasses;
using SD.HnD.DAL;
using SD.HnD.DAL.TypedListClasses;
using SD.HnD.DAL.FactoryClasses;
using SD.HnD.DAL.CollectionClasses;
using SD.HnD.DAL.EntityClasses;
using SD.HnD.DAL.HelperClasses;
using SD.HnD.DAL.DaoClasses;
using SD.LLBLGen.Pro.QuerySpec;
using SD.LLBLGen.Pro.QuerySpec.SelfServicing;

using SD.HnD.Utility;
using System.Collections.Generic;
using System.Data.Common;

namespace SD.HnD.BL
{
	/// <summary>
	/// Generic searcher.
	/// This searcher class uses the MS full text search engine to find matching messages.
	/// </summary>
	public static class Searcher
	{
		/// <summary>
		/// Does the search using MS Full text search
		/// </summary>
		/// <param name="searchString">Search string.</param>
		/// <param name="forumIDs">Forum Ids of forums to search into.</param>
		/// <param name="orderFirstElement">Order first element setting.</param>
		/// <param name="orderSecondElement">Order second element setting.</param>
		/// <param name="forumsWithThreadsFromOthers">The forums with threads from others.</param>
		/// <param name="userID">The userid of the calling user.</param>
		/// <param name="targetToSearch">The target to search.</param>
		/// <returns>
		/// TypedList filled with threads matching the query.
		/// </returns>
		public static SearchResultTypedList DoSearch(string searchString, List<int> forumIDs, SearchResultsOrderSetting orderFirstElement,
			SearchResultsOrderSetting orderSecondElement, List<int> forumsWithThreadsFromOthers, int userID, SearchTarget targetToSearch)
		{
			// the search utilizes full text search. It performs a CONTAINS upon the MessageText field of the Message entity. 
			string searchTerms = PrepareSearchTerms(searchString);
			bool searchMessageText = (targetToSearch == SearchTarget.MessageText) || (targetToSearch == SearchTarget.MessageTextAndThreadSubject);
			bool searchSubject = (targetToSearch == SearchTarget.ThreadSubject) || (targetToSearch == SearchTarget.MessageTextAndThreadSubject);
			if(!(searchSubject || searchMessageText))
			{
				// no target specified, select message
				searchMessageText = true;
			}

			PredicateExpression searchTermFilter = new PredicateExpression();
			if(searchMessageText)
			{
				// Message contents filter
				searchTermFilter.Add(new FieldCompareSetPredicate(ThreadFields.ThreadID, MessageFields.ThreadID,
									SetOperator.In, new FieldFullTextSearchPredicate(MessageFields.MessageText, FullTextSearchOperator.Contains, searchTerms)));
			}
			if(searchSubject)
			{
				// Thread subject filter
				if(searchMessageText)
				{
					searchTermFilter.AddWithOr(new FieldFullTextSearchPredicate(ThreadFields.Subject, FullTextSearchOperator.Contains, searchTerms));
				}
				else
				{
					searchTermFilter.Add(new FieldFullTextSearchPredicate(ThreadFields.Subject, FullTextSearchOperator.Contains, searchTerms));
				}
			}
			IPredicateExpression mainFilter = searchTermFilter
													.And(ForumFields.ForumID == forumIDs)
													.And(ThreadGuiHelper.CreateThreadFilter(forumsWithThreadsFromOthers, userID));

			ISortExpression sorter = new SortExpression();
			// add first element
			sorter.Add(CreateSearchSortClause(orderFirstElement));
			if(orderSecondElement != orderFirstElement)
			{
				sorter.Add(CreateSearchSortClause(orderSecondElement));
			}

			SearchResultTypedList results = new SearchResultTypedList(false);

			try
			{
				// get the data from the db. 
				results.Fill(500, sorter, false, mainFilter);
			}
			catch
			{
				// probably an error with the search words / user error. Swallow for now, which will result in an empty resultset.
			}

			return results;
		}



		/// <summary>
		/// Prepares the search terms.
		/// </summary>
		/// <param name="searchTerms">Search terms.</param>
		/// <returns>search terms string, prepare for CONTAINS usage.</returns>
		private static string PrepareSearchTerms(string searchTerms)
		{
			ArrayList termsToProcess = new ArrayList();
			string[] terms = searchTerms.Split(' ');
			// now traverse from front to back. Collide any sequence of terms where the start term starts with a '"' and the end term ends with a '"'.
			for(int i=0;i<terms.Length;i++)
			{
				string term = terms[i];
				if(term.Length<=0)
				{
					// dangling space
					continue;
				}

				if(term.StartsWith("\""))
				{
					// start of sequence, find end of sequence.
					bool endOfSequenceFound = false;
					StringBuilder tmpTerm = new StringBuilder(256);
					int endIndexOfSequence = i;
					for (int j = i; j < terms.Length; j++)
					{
						if(terms[j].EndsWith("\""))
						{
							// end of sequence found, collide
							endOfSequenceFound=true;
							bool firstTermSeen = false;
							for(int k=i;k<=j;k++)
							{
								if(firstTermSeen)
								{
									tmpTerm.Append(" ");
								}
								tmpTerm.Append(terms[k]);
								firstTermSeen=true;
							}
							endIndexOfSequence = j;
							break;
						}
					}

					if(endOfSequenceFound)
					{
						// sequence found, append as one element
						termsToProcess.Add(tmpTerm.ToString());
						i=endIndexOfSequence;
					}
					else
					{
						// dangling quote. 
						termsToProcess.Add(term);
					}
				}
				else
				{
					// single term, simply add it.
					termsToProcess.Add(term);
				}
			}

			// now rebuild the searchTerms. We insert 'AND' if no operator is present and we surround wildcard searches with '"' if no
			// '"' is present.
			StringBuilder toReturn = new StringBuilder(searchTerms.Length+(5*termsToProcess.Count));
			bool operatorSeenLastIteration = false;
			for (int i = 0; i < termsToProcess.Count; i++)
			{
				string term = (string)termsToProcess[i];
				string termLowerCase = term.ToLowerInvariant();
				// check if this is an operator.
				switch(termLowerCase)
				{
					case "or":
					case "and":
						// operator, so emit it and set the flag we've seen an operator so next element won't emit AND, and continue with next element.
						if(i > 0)
						{
							operatorSeenLastIteration = true;
							toReturn.AppendFormat(" {0} ", term);
						}
						continue;
					case "not":
						// emit an operator if none seen, and make the next element think an operator was just emitted
						if(i > 0)
						{
							if(!operatorSeenLastIteration)
							{
								// last iteration wasn't an operator, emit 'AND'
								toReturn.Append(" AND ");
							}
						}
						operatorSeenLastIteration = true;
						break;
					default:
						// not an operator nor 'not', so emit an operator if we have to. 
						if(i > 0)
						{
							if(!operatorSeenLastIteration)
							{
								// last iteration wasn't an operator, emit 'AND'
								toReturn.Append(" AND ");
							}
							operatorSeenLastIteration = false;
						}
						break;
				}
				if(term.StartsWith("*") || term.EndsWith("*"))
				{
					// wildcard without proper quotes
					toReturn.AppendFormat(" \"{0}\" ", term);
				}
				else
				{
					toReturn.AppendFormat(" {0} ", term);
				}
			}

			return toReturn.ToString();
		}


		/// <summary>
		/// Creates the search sort clause for the element passed in
		/// </summary>
		/// <param name="element">Element.</param>
		/// <returns>SortClause object to be used in a sort expression</returns>
		private static ISortClause CreateSearchSortClause(SearchResultsOrderSetting element)
		{
			ISortClause toReturn = null;
			switch(element)
			{
				case SearchResultsOrderSetting.ForumAscending:					
					toReturn = ForumFields.ForumName.Ascending();
					break;
				case SearchResultsOrderSetting.ForumDescending:
					toReturn = ForumFields.ForumName.Descending();
					break;
				case SearchResultsOrderSetting.LastPostDateAscending:
					toReturn = ThreadFields.ThreadLastPostingDate.Ascending();
					break;
				case SearchResultsOrderSetting.LastPostDateDescending:
					toReturn = ThreadFields.ThreadLastPostingDate.Descending();
					break;
				case SearchResultsOrderSetting.ThreadSubjectAscending:
					toReturn = ThreadFields.Subject.Ascending();
					break;
				case SearchResultsOrderSetting.ThreadSubjectDescending:
					toReturn = ThreadFields.Subject.Descending();
					break;
			}

			return toReturn;
		}
	}
}
