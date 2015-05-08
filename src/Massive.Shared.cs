using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Dynamic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Massive
{
	/// <summary>
	/// Class which provides extension methods for various ADO.NET objects.
	/// </summary>
	public static partial class ObjectExtensions
	{
		/// <summary>
		/// Extension method for adding in a bunch of parameters
		/// </summary>
		/// <param name="cmd">The command to add the parameters to.</param>
		/// <param name="args">The parameter values to convert to parameters.</param>
		public static void AddParams(this DbCommand cmd, params object[] args)
		{
			if(args == null)
			{
				return;
			}
			foreach(var item in args)
			{
				AddParam(cmd, item);
			}
		}


		/// <summary>
		/// Turns an IDataReader to a Dynamic list of things
		/// </summary>
		/// <param name="reader">The datareader which rows to convert to a list of expandos.</param>
		/// <returns>List of expandos, one for every row read.</returns>
		public static List<dynamic> ToExpandoList(this IDataReader reader)
		{
			var result = new List<dynamic>();
			while(reader.Read())
			{
				result.Add(reader.RecordToExpando());
			}
			return result;
		}


		/// <summary>
		/// Converts the current row the datareader points to to a new Expando object.
		/// </summary>
		/// <param name="reader">The RDR.</param>
		/// <returns>expando object which contains the values of the row the reader points to</returns>
		public static dynamic RecordToExpando(this IDataReader reader)
		{
			dynamic e = new ExpandoObject();
			var d = (IDictionary<string, object>)e;
			object[] values = new object[reader.FieldCount];
			reader.GetValues(values);
			for(int i = 0; i < values.Length; i++)
			{
				var v = values[i];
				d.Add(reader.GetName(i), DBNull.Value.Equals(v) ? null : v);
			}
			return e;
		}


		/// <summary>
		/// Turns the object into an ExpandoObject 
		/// </summary>
		/// <param name="o">The object to convert.</param>
		/// <returns>a new expando object with the values of the passed in object</returns>
		public static dynamic ToExpando(this object o)
		{
			if(o is ExpandoObject)
			{
				return o;
			}
			var result = new ExpandoObject();
			var d = (IDictionary<string, object>)result; //work with the Expando as a Dictionary
			if(o.GetType() == typeof(NameValueCollection) || o.GetType().IsSubclassOf(typeof(NameValueCollection)))
			{
				var nv = (NameValueCollection)o;
				nv.Cast<string>().Select(key => new KeyValuePair<string, object>(key, nv[key])).ToList().ForEach(i => d.Add(i));
			}
			else
			{
				var props = o.GetType().GetProperties();
				foreach(var item in props)
				{
					d.Add(item.Name, item.GetValue(o, null));
				}
			}
			return result;
		}


		/// <summary>
		/// Turns the object into a Dictionary with for each property a name-value pair, with name as key.
		/// </summary>
		/// <param name="thingy">The object to convert to a dictionary.</param>
		/// <returns></returns>
		public static IDictionary<string, object> ToDictionary(this object thingy)
		{
			return (IDictionary<string, object>)thingy.ToExpando();
		}
	}


	/// <summary>
	/// Convenience class for opening/executing data
	/// </summary>
	public static class DB
	{
		public static DynamicModel Current
		{
			get
			{
				if(ConfigurationManager.ConnectionStrings.Count > 1)
				{
					return new DynamicModel(ConfigurationManager.ConnectionStrings[1].Name);
				}
				throw new InvalidOperationException("Need a connection string name - can't determine what it is");
			}
		}
	}


	/// <summary>
	/// A class that wraps your database table in Dynamic Funtime
	/// </summary>
	public partial class DynamicModel : DynamicObject
	{
		private DbProviderFactory _factory;
		private string _connectionString;
		private IEnumerable<dynamic> _schema;


		/// <summary>
		/// Gets a default value for the column with the name specified as defined in the schema.
		/// </summary>
		/// <param name="columnName">Name of the column.</param>
		/// <returns></returns>
		public dynamic DefaultValue(string columnName)
		{
			var column = GetColumn(columnName);
			if(column == null)
			{
				return null;
			}
			return GetDefaultValue(column);
		}


		/// <summary>
		/// Gets or creates a new, empty DynamicModel on the DB pointed to by the connectionstring stored under the name specified.
		/// </summary>
		/// <param name="connectionStringName">Name of the connection string to load from the config file.</param>
		/// <returns>ready to use, empty DynamicModel</returns>
		public static DynamicModel Open(string connectionStringName)
		{
			return new DynamicModel(connectionStringName);
		}


		/// <summary>
		/// Initializes a new instance of the <see cref="DynamicModel"/> class.
		/// </summary>
		/// <param name="connectionStringName">Name of the connection string to load from the config file.</param>
		/// <param name="tableName">Name of the table to read the meta data for. Can be left empty, in which case the name of this type is used.</param>
		/// <param name="primaryKeyField">The primary key field. Can be left empty, in which case 'ID' is used.</param>
		/// <param name="descriptorField">The descriptor field, if the table is a lookup table. Descriptor field is the field containing the textual representation of the value
		/// in primaryKeyField.</param>
		public DynamicModel(string connectionStringName, string tableName = "", string primaryKeyField = "", string descriptorField = "")
		{
			this.TableName = string.IsNullOrWhiteSpace(tableName) ? this.GetType().Name : tableName;
			this.PrimaryKeyField = string.IsNullOrWhiteSpace(primaryKeyField) ? "ID" : primaryKeyField;
			this.DescriptorField = descriptorField;
			var _providerName = this.DbProviderFactoryName;
			this.Errors = new List<string>();

			if(!string.IsNullOrWhiteSpace(ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName))
			{
				_providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
			}
			_factory = DbProviderFactories.GetFactory(_providerName);
			_connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
		}


		/// <summary>
		/// Creates a new Expando from a Form POST - white listed against the columns in the DB, only setting values which names are in the schema.
		/// </summary>
		/// <param name="coll">The name-value collection coming from an external source, e.g. a POST.</param>
		/// <returns>new expando object with the fields as defined in the schema and with the values as specified in the collection passed in</returns>
		public dynamic CreateFrom(NameValueCollection coll)
		{
			dynamic result = new ExpandoObject();
			var dc = (IDictionary<string, object>)result;
			//loop the collection, setting only what's in the Schema
			foreach(var item in coll.Keys)
			{
				var columnName = item.ToString();
				if(this.ColumnExists(columnName))
				{
					dc.Add(columnName, coll[columnName]);
				}
			}
			return result;
		}


		/// <summary>
		/// Enumerates the reader yielding the result - thanks to Jeroen Haegebaert
		/// </summary>
		/// <param name="sql">The SQL to execute as a command.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns>streaming enumerable with expandos, one for each row read</returns>
		public virtual IEnumerable<dynamic> Query(string sql, params object[] args)
		{
			using(var conn = OpenConnection())
			{
				using(var rdr = CreateCommand(sql, conn, args).ExecuteReader())
				{
					while(rdr.Read())
					{
						yield return rdr.RecordToExpando();
					}
					rdr.Close();
				}
				conn.Close();
			}
		}


		/// <summary>
		/// Enumerates the reader yielding the result - thanks to Jeroen Haegebaert
		/// </summary>
		/// <param name="sql">The SQL to execute as a command.</param>
		/// <param name="connection">The connection to use with the command.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns>streaming enumerable with expandos, one for each row read</returns>
		public virtual IEnumerable<dynamic> Query(string sql, DbConnection connection, params object[] args)
		{
			using(var rdr = CreateCommand(sql, connection, args).ExecuteReader())
			{
				while(rdr.Read())
				{
					yield return rdr.RecordToExpando(); ;
				}
				rdr.Close();
			}
		}


		/// <summary>
		/// Returns a single result by executing the passed in query + parameters as a scalar query.
		/// </summary>
		/// <param name="sql">The SQL to execute as a scalar command.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns>first value returned from the query executed or null of no result was returned by the database.</returns>
		public virtual object Scalar(string sql, params object[] args)
		{
			object result;
			using(var conn = OpenConnection())
			{
				result = CreateCommand(sql, conn, args).ExecuteScalar();
				conn.Close();
			}
			return result;
		}


		/// <summary>
		/// Returns and OpenConnection
		/// </summary>
		public virtual DbConnection OpenConnection()
		{
			var result = _factory.CreateConnection();
			if(result != null)
			{
				result.ConnectionString = _connectionString;
				result.Open();
			}
			return result;
		}


		/// <summary>
		/// Builds a set of Insert and Update commands based on the passed-on objects. These objects can be POCOs, Anonymous, NameValueCollections, or Expandos. Objects
		/// With a PK property (whatever PrimaryKeyField is set to) will be created at UPDATEs
		/// </summary>
		public virtual List<DbCommand> BuildCommands(params object[] things)
		{
			var commands = new List<DbCommand>();
			foreach(var item in things)
			{
				if(HasPrimaryKey(item))
				{
					commands.Add(CreateUpdateCommand(item.ToExpando(), GetPrimaryKey(item)));
				}
				else
				{
					commands.Add(CreateInsertCommand(item.ToExpando()));
				}
			}
			return commands;
		}


		/// <summary>
		/// Executes the specified command using a new connection
		/// </summary>
		/// <param name="command">The command to execute.</param>
		/// <returns>the value returned by the database after executing the command. </returns>
		public virtual int Execute(DbCommand command)
		{
			return Execute(new DbCommand[] { command });
		}


		/// <summary>
		/// Executes the specified SQL as a new command using a new connection. 
		/// </summary>
		/// <param name="sql">The SQL statement to execute as a command.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns>the value returned by the database after executing the command. </returns>
		public virtual int Execute(string sql, params object[] args)
		{
			return Execute(CreateCommand(sql, null, args));
		}


		/// <summary>
		/// Executes a series of DBCommands in a new transaction using a new connection
		/// </summary>
		/// <param name="commands">The commands to execute.</param>
		/// <returns>the sum of the values returned by the database when executing each command.</returns>
		public virtual int Execute(IEnumerable<DbCommand> commands)
		{
			var result = 0;
			using(var conn = OpenConnection())
			{
				using(var tx = conn.BeginTransaction())
				{
					foreach(var cmd in commands)
					{
						cmd.Connection = conn;
						cmd.Transaction = tx;
						result += cmd.ExecuteNonQuery();
					}
					tx.Commit();
				}
				conn.Close();
			}
			return result;
		}


		/// <summary>
		/// Conventionally introspects the object passed in for a field that looks like a PK. If you've named your PrimaryKeyField, this becomes easy
		/// </summary>
		public virtual bool HasPrimaryKey(object o)
		{
			return o.ToDictionary().ContainsKey(PrimaryKeyField);
		}


		/// <summary>
		/// If the object passed in has a property with the same name as your PrimaryKeyField it is returned here.
		/// </summary>
		public virtual object GetPrimaryKey(object o)
		{
			object result;
			o.ToDictionary().TryGetValue(PrimaryKeyField, out result);
			return result;
		}


		/// <summary>
		/// Returns all records complying with the passed-in WHERE clause and arguments, ordered as specified, limited by limit specified using the DB specific limit system.
		/// </summary>
		/// <param name="where">The where clause. Default is empty string. Parameters have to be numbered starting with 0, for each value in args.</param>
		/// <param name="orderBy">The order by clause. Default is empty string.</param>
		/// <param name="limit">The limit. Default is 0 (no limit).</param>
		/// <param name="columns">The columns to use in the project. Default is '*' (all columns, in table defined order).</param>
		/// <param name="args">The values to use as parameters.</param>
		/// <returns>streaming enumerable with expandos, one for each row read</returns>
		public virtual IEnumerable<dynamic> All(string where = "", string orderBy = "", int limit = 0, string columns = "*", params object[] args)
		{
			return Query(string.Format(BuildSelectQueryPattern(@where, orderBy, limit), columns, TableName), args);
		}


		/// <summary>
		/// Fetches a dynamic PagedResult. 
		/// </summary>
		/// <param name="where">The where clause. Default is empty string. Parameters have to be numbered starting with 0, for each value in args.</param>
		/// <param name="orderBy">The order by clause. Default is empty string.</param>
		/// <param name="columns">The columns to use in the project. Default is '*' (all columns, in table defined order).</param>
		/// <param name="pageSize">Size of the page. Default is 20</param>
		/// <param name="currentPage">The current page. 1-based. Default is 1.</param>
		/// <param name="args">The values to use as parameters.</param>
		/// <returns>The result of the paged query. Result properties are Items, TotalPages, and TotalRecords.</returns>
		public virtual dynamic Paged(string where = "", string orderBy = "", string columns = "*", int pageSize = 20, int currentPage = 1, params object[] args)
		{
			return BuildPagedResult(where: where, orderBy: orderBy, columns: columns, pageSize: pageSize, currentPage: currentPage, args: args);
		}


		/// <summary>
		/// Fetches a dynamic PagedResult.
		/// </summary>
		/// <param name="sql">The SQL statement to use as query over which resultset is paged.</param>
		/// <param name="primaryKey">The primary key to use for ordering. Can be left empty</param>
		/// <param name="where">The where clause. Default is empty string. Parameters have to be numbered starting with 0, for each value in args.</param>
		/// <param name="orderBy">The order by clause. Default is empty string.</param>
		/// <param name="columns">The columns to use in the project. Default is '*' (all columns, in table defined order).</param>
		/// <param name="pageSize">Size of the page. Default is 20</param>
		/// <param name="currentPage">The current page. 1-based. Default is 1.</param>
		/// <param name="args">The values to use as parameters.</param>
		/// <returns>
		/// The result of the paged query. Result properties are Items, TotalPages, and TotalRecords.
		/// </returns>
		public virtual dynamic Paged(string sql, string primaryKey, string where = "", string orderBy = "", string columns = "*", int pageSize = 20, int currentPage = 1, params object[] args)
		{
			return BuildPagedResult(sql, primaryKey, where, orderBy, columns, pageSize, currentPage, args);
		}


		/// <summary>
		/// Returns a single row from the database
		/// </summary>
		public virtual dynamic Single(string where, params object[] args)
		{
			return All(where, limit:1, args:args).FirstOrDefault();
		}


		/// <summary>
		/// Returns a single row from the database
		/// </summary>
		public virtual dynamic Single(object key, string columns = "*")
		{
			return All(this.GetPkComparisonPredicateQueryFragment(), limit: 1, columns: columns, args: new[] {key}).FirstOrDefault();
		}


		/// <summary>
		/// This will return a string/object dictionary for dropdowns etc
		/// </summary>
		public virtual IDictionary<string, object> KeyValues(string orderBy = "")
		{
			if(string.IsNullOrEmpty(DescriptorField))
			{
				throw new InvalidOperationException("There's no DescriptorField set - do this in your constructor to describe the text value you want to see");
			}
			var results = All(orderBy:orderBy, columns:string.Format("{0}, {1}", this.PrimaryKeyField, this.DescriptorField)).ToList().Cast<IDictionary<string, object>>();
			return results.ToDictionary(key => key[PrimaryKeyField].ToString(), value => value[DescriptorField]);
		}


		/// <summary>
		/// This will return an Expando as a Dictionary. This method does a cast to an interface through a method call, which means it's ... rather useless.
		/// </summary>
		/// <param name="item">The item to convert</param>
		/// <returns></returns>
		public virtual IDictionary<string, object> ItemAsDictionary(ExpandoObject item)
		{
			return item;
		}


		/// <summary>
		/// Checks to see if a key is present based on the passed-in value
		/// </summary>
		/// <param name="key">The key to search for.</param>
		/// <param name="item">The expando object to search for the key.</param>
		/// <returns>true if the passed in expando object contains key, false otherwise</returns>
		public virtual bool ItemContainsKey(string key, ExpandoObject item)
		{
			return ((IDictionary<string, object>)item).ContainsKey(key);
		}


		/// <summary>
		/// Executes a set of objects as Insert or Update commands based on their property settings, within a transaction. These objects can be POCOs, Anonymous, NameValueCollections, 
		/// or Expandos. Objects with a PK property (whatever PrimaryKeyField is set to) will be created at UPDATEs
		/// </summary>
		/// <param name="things">The objects to save within a single transaction.</param>
		/// <returns>the sum of the values returned by the database when executing each command.</returns>
		public virtual int Save(params object[] things)
		{
			if(things.Any(item=>!IsValid(item)))
			{
				throw new InvalidOperationException("Can't save this item: " + String.Join("; ", this.Errors.ToArray()));
			}
			return Execute(BuildCommands(things));
		}


		/// <summary>
		/// Creates a DbCommand with an insert statement to insert a new row in the table, using the values in the passed in expando.
		/// </summary>
		/// <param name="expando">The expando object which contains per field the value to insert.</param>
		/// <returns>ready to use DbCommand</returns>
		/// <exception cref="System.InvalidOperationException">Can't parse this object to the database - there are no properties set</exception>
		public virtual DbCommand CreateInsertCommand(dynamic expando)
		{
			var settings = (IDictionary<string, object>)expando;
			var fieldNames = new List<string>();
			var valueParameters = new List<string>();
			var insertQueryPattern = this.GetInsertQueryPattern();
			var result = CreateCommand(insertQueryPattern, null);
			int counter = 0;
			foreach(var item in settings)
			{
				fieldNames.Add(item.Key);
				valueParameters.Add(this.PrefixParameterName(counter.ToString()));
				result.AddParam(item.Value);
				counter++;
			}
			if(counter > 0)
			{
				result.CommandText = string.Format(insertQueryPattern, TableName, string.Join(", ", fieldNames.ToArray()), string.Join(", ", valueParameters.ToArray()));
			}
			else
			{
				throw new InvalidOperationException("Can't parse this object to the database - there are no properties set");
			}
			return result;
		}


		/// <summary>
		/// Creates a DbCommand with an update command to update an existing row in the table, using the values in the specified expando.
		/// </summary>
		/// <param name="expando">The expando with the fields to update.</param>
		/// <param name="key">The key value to use for PrimarykeyField comparison.</param>
		/// <returns>ready to use DbCommand</returns>
		public virtual DbCommand CreateUpdateCommand(dynamic expando, object key)
		{
			return CreateUpdateWhereCommand(expando, string.Format("{0} = {1}", this.PrimaryKeyField, this.PrefixParameterName("0")), key);
		}


		/// <summary>
		/// Creates a DbCommand with an update command to update an existing row in the table, using the values in the specified expando.
		/// </summary>
		/// <param name="expando">The expando with the fields to update.</param>
		/// <param name="where">The where clause. Default is empty string. Parameters have to be numbered starting with 0, for each value in args.</param>
		/// <param name="args">The parameter values to use.</param>
		/// <returns>
		/// ready to use DbCommand
		/// </returns>
		/// <exception cref="System.InvalidOperationException">No parsable object was sent in - could not define any name/value pairs</exception>
		public virtual DbCommand CreateUpdateWhereCommand(dynamic expando, string where = "", params object[] args)
		{
			var settings = (IDictionary<string, object>)expando;
			var fieldSetFragments = new List<string>();
			var updateQueryPattern = this.GetUpdateQueryPattern();
			if(!string.IsNullOrWhiteSpace(@where))
			{
				updateQueryPattern += @where.Trim().StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) ? string.Empty : " WHERE ";
			}
			var result = CreateCommand(updateQueryPattern, null, args);
			int counter = args.Length > 0 ? args.Length : 0;
			foreach(var item in settings)
			{
				var val = item.Value;
				if(!item.Key.Equals(PrimaryKeyField, StringComparison.OrdinalIgnoreCase) && item.Value != null)
				{
					result.AddParam(val);
					fieldSetFragments.Add(string.Format("{0} = {1}", item.Key, this.PrefixParameterName(counter.ToString())));
					counter++;
				}
			}
			if(counter > 0)
			{
				result.CommandText = string.Format(updateQueryPattern, TableName, string.Join(", ", fieldSetFragments.ToArray()));
			}
			else
			{
				throw new InvalidOperationException("No parsable object was sent in - could not define any name/value pairs");
			}
			return result;
		}


		/// <summary>
		/// Creates a DbCommand with a delete statement to delete one or more records from the DB according to the passed-in where clause/key value. 
		/// </summary>
		/// <param name="where">The where clause. Can be empty. Ignored if key is set.</param>
		/// <param name="key">The key. Value to compare with the PrimaryKeyField. If null, <see cref="where"/> is used as the where clause.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns>ready to use DbCommand</returns>
		public virtual DbCommand CreateDeleteCommand(string where = "", object key = null, params object[] args)
		{
			var sql = string.Format(this.GetDeleteQueryPattern(), TableName);
			if(key != null)
			{
				sql += string.Format("WHERE {0}={1}", this.PrimaryKeyField, this.PrefixParameterName("0"));
				args = new[] { key };
			}
			else
			{
				if(!string.IsNullOrEmpty(where))
				{
					sql += where.Trim().StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) ? where : "WHERE " + where;
				}
			}
			return CreateCommand(sql, null, args);
		}


		/// <summary>
		/// Adds a record to the database. You can pass in an Anonymous object, an ExpandoObject,
		/// A regular old POCO, or a NameValueColletion from a Request.Form or Request.QueryString
		/// </summary>
		/// <param name="o">The object to insert.</param>
		/// <returns>the object inserted as expando</returns>
		public virtual dynamic Insert(object o)
		{
			var ex = o.ToExpando();
			if(!IsValid(ex))
			{
				throw new InvalidOperationException("Can't insert: " + String.Join("; ", Errors.ToArray()));
			}
			if(BeforeSave(ex))
			{
				using(var conn = OpenConnection())
				{
					var cmd = CreateInsertCommand(ex);
					cmd.Connection = conn;
					cmd.ExecuteNonQuery();
					cmd.CommandText = this.GetIdentityRetrievalScalarStatement();
					ex.ID = cmd.ExecuteScalar();
					Inserted(ex);
					conn.Close();
				}
				return ex;
			}
			return null;
		}


		/// <summary>
		/// Updates a record in the database. You can pass in an Anonymous object, an ExpandoObject,
		/// A regular old POCO, or a NameValueCollection from a Request.Form or Request.QueryString
		/// </summary>
		/// <param name="o">The object to update</param>
		/// <param name="key">The key value to compare against PrimaryKeyField.</param>
		/// <returns>the number returned by the database after executing the update command </returns>
		public virtual int Update(object o, object key)
		{
			var ex = o.ToExpando();
			if(!IsValid(ex))
			{
				throw new InvalidOperationException("Can't Update: " + String.Join("; ", Errors.ToArray()));
			}
			var result = 0;
			if(BeforeSave(ex))
			{
				result = Execute(CreateUpdateCommand(ex, key));
				Updated(ex);
			}
			return result;
		}


		/// <summary>
		/// Updates a all records in the database that match where clause. You can pass in an Anonymous object, an ExpandoObject,
		/// A regular old POCO, or a NameValueCollection from a Request.Form or Request.QueryString. Where works same same as in All().
		/// </summary>
		/// <param name="o">The object to update</param>
		/// <param name="where">The where clause. Default is empty string. Parameters have to be numbered starting with 0, for each value in args.</param>
		/// <param name="args">The parameters used in the where clause.</param>
		/// <returns>the number returned by the database after executing the update command </returns>
		public virtual int Update(object o, string where = "1=1", params object[] args)
		{
			if(string.IsNullOrWhiteSpace(where))
			{
				return 0;
			}
			var ex = o.ToExpando();
			if(!IsValid(ex))
			{
				throw new InvalidOperationException("Can't Update: " + String.Join("; ", Errors.ToArray()));
			}
			var result = 0;
			if(BeforeSave(ex))
			{
				result = Execute(CreateUpdateWhereCommand(ex, where, args));
				Updated(ex);
			}
			return result;
		}


		/// <summary>
		/// Deletes one or more records from the DB according to the passed-in where clause/key value. 
		/// </summary>
		/// <param name="key">The key. Value to compare with the PrimaryKeyField. If null, <see cref="where"/> is used as the where clause.</param>
		/// <param name="where">The where clause. Can be empty. Ignored if key is set.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns></returns>
		public int Delete(object key = null, string where = "", params object[] args)
		{
			var deleted = this.Single(key);
			var result = 0;
			if(BeforeDelete(deleted))
			{
				result = Execute(CreateDeleteCommand(where, key, args));
				Deleted(deleted);
			}
			return result;
		}


		/// <summary>
		/// Adds the value to item with the name stored in key, if item doesn't already contains a field with the name in key
		/// </summary>
		/// <param name="key">The key.</param>
		/// <param name="value">The value.</param>
		/// <param name="item">The item.</param>
		public void DefaultTo(string key, object value, dynamic item)
		{
			if(!ItemContainsKey(key, item))
			{
				var dc = (IDictionary<string, object>)item;
				dc[key] = value;
			}
		}


		/// <summary>
		/// Hook, called when IsValid is called
		/// </summary>
		/// <param name="item">The item to validate.</param>
		public virtual void Validate(dynamic item) { }
		/// <summary>
		/// Hook, called after item has been inserted.
		/// </summary>
		/// <param name="item">The item inserted.</param>
		public virtual void Inserted(dynamic item) { }
		/// <summary>
		/// Hook, called after item has been updated
		/// </summary>
		/// <param name="item">The item updated.</param>
		public virtual void Updated(dynamic item) { }
		/// <summary>
		/// Hook, called after item has been deleted.
		/// </summary>
		/// <param name="item">The item deleted.</param>
		public virtual void Deleted(dynamic item) { }
		/// <summary>
		/// Hook, called before item will be deleted.
		/// </summary>
		/// <param name="item">The item to be deleted.</param>
		/// <returns>true if delete can proceed, false if it can't</returns>
		public virtual bool BeforeDelete(dynamic item) { return true; }
		/// <summary>
		/// Hook, called before item will be saved
		/// </summary>
		/// <param name="item">The item to save.</param>
		/// <returns>true if save can proceed, false if it can't</returns>
		public virtual bool BeforeSave(dynamic item) { return true; }


		/// <summary>
		/// Determines whether the specified item is valid. Errors are stored in the Errors property
		/// </summary>
		/// <param name="item">The item to validate.</param>
		/// <returns>true if valid (0 errors), false otherwise</returns>
		public bool IsValid(dynamic item)
		{
			Errors.Clear();
			Validate(item);
			return Errors.Count == 0;
		}


		/// <summary>
		/// Validates if the value specified is null or not. If it is null, the message is added to Errors.
		/// </summary>
		/// <param name="value">The value to check.</param>
		/// <param name="message">The message to log as Error. Default is 'Required'.</param>
		public virtual void ValidatesPresenceOf(object value, string message = "Required")
		{
			if((value == null) || string.IsNullOrEmpty(value.ToString()))
			{
				Errors.Add(message);
			}
		}


		/// <summary>
		/// Validates whether the value specified is numeric (short, int, long, double, single and float). If not, message is logged in Errors.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <param name="message">The message to log as Error. Default is 'Required'.</param>
		public virtual void ValidatesNumericalityOf(object value, string message = "Should be a number")
		{
			var numerics = new[] { "Int32", "Int16", "Int64", "Decimal", "Double", "Single", "Float" };
			if((value==null) || !numerics.Contains(value.GetType().Name))
			{
				Errors.Add(message);
			}
		}


		/// <summary>
		/// Executes a Count(*) query on the Table
		/// </summary>
		/// <returns>number of rows returned after executing the count query</returns>
		public int Count()
		{
			return Count(TableName);
		}


		/// <summary>
		/// Executes a Count(*) query on the Tablename specified using the where clause specified
		/// </summary>
		/// <param name="tableName">Name of the table to execute the count query on. By default it's this table's name</param>
		/// <param name="where">The where clause. Default is empty string. Parameters have to be numbered starting with 0, for each value in args.</param>
		/// <param name="args">The parameters used in the where clause.</param>
		/// <returns>number of rows returned after executing the count query</returns>
		public int Count(string tableName = "", string where = "", params object[] args)
		{
			var tableNameToUse = string.IsNullOrEmpty(tableName) ? this.TableName : tableName;
			var scalarQueryPattern = this.GetCountRowQueryPattern();
			if(!string.IsNullOrEmpty(where))
			{
				scalarQueryPattern += where.Trim().StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) ? where : "WHERE " + where;
			}
			return (int)Scalar(string.Format(scalarQueryPattern, tableNameToUse), args);
		}


		/// <summary>
		/// Provides the implementation for operations that invoke a member. This method implementation tries to create queries from the methods being invoked based on the name
		/// of the invoked method.
		/// </summary>
		/// <param name="binder">Provides information about the dynamic operation. The binder.Name property provides the name of the member on which the dynamic operation is performed. For example, for the statement sampleObject.SampleMethod(100), where sampleObject is an instance of the class derived from the <see cref="T:System.Dynamic.DynamicObject" /> class, binder.Name returns "SampleMethod". The binder.IgnoreCase property specifies whether the member name is case-sensitive.</param>
		/// <param name="args">The arguments that are passed to the object member during the invoke operation. For example, for the statement sampleObject.SampleMethod(100), where sampleObject is derived from the <see cref="T:System.Dynamic.DynamicObject" /> class, <paramref name="args[0]" /> is equal to 100.</param>
		/// <param name="result">The result of the member invocation.</param>
		/// <returns>
		/// true if the operation is successful; otherwise, false. If this method returns false, the run-time binder of the language determines the behavior. (In most cases, a language-specific run-time exception is thrown.)
		/// </returns>
		public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
		{
			var counter = 0;
			var info = binder.CallInfo;
			if(info.ArgumentNames.Count != args.Length)
			{
				throw new InvalidOperationException("Please use named arguments for this type of query - the column name, orderby, columns, etc");
			}
			//first should be "FindBy, Last, Single, First"
			var op = binder.Name;
			var columns = " * ";
			string orderBy = string.Format(" ORDER BY {0}", PrimaryKeyField);
			string where = string.Empty;
			var whereArgs = new List<object>();

			//loop the named args - see if we have order, columns and constraints
			var constraints = new List<string>();
			if(info.ArgumentNames.Count > 0)
			{
				for(int i = 0; i < args.Length; i++)
				{
					var name = info.ArgumentNames[i].ToLowerInvariant();
					switch(name)
					{
						case "orderby":
							orderBy = " ORDER BY " + args[i];
							break;
						case "columns":
							columns = args[i].ToString();
							break;
						default:
							constraints.Add(string.Format(" {0} = {1}", name, this.PrefixParameterName(counter.ToString())));
							whereArgs.Add(args[i]);
							counter++;
							break;
					}
				}
			}

			//Build the WHERE bits
			if(constraints.Count > 0)
			{
				where = " WHERE " + string.Join(" AND ", constraints.ToArray());
			}

			string oplowercase = op.ToLowerInvariant();
			result = null;
			switch(oplowercase)
			{
				case "count":
					result = Count(TableName, @where, whereArgs.ToArray());
					break;
				case "sum":
				case "max":
				case "min":
				case "avg":
					var aggregate = this.GetAggregateFunction(oplowercase);
					if(!string.IsNullOrWhiteSpace(aggregate))
					{
						result = Scalar(string.Format("SELECT {0}({1}) FROM {2} {3}", aggregate, columns, this.TableName, @where), whereArgs.ToArray());
					}
					break;
				default:
					//build the SQL
					var justOne = op.StartsWith("First") || op.StartsWith("Last") || op.StartsWith("Get") || op.StartsWith("Find") || op.StartsWith("Single");
					//Be sure to sort by DESC on the PK (PK Sort is the default)
					if(op.StartsWith("Last"))
					{
						orderBy = orderBy + " DESC ";
					}
					result = justOne ? All(@where, orderBy, 1, columns, whereArgs.ToArray()).FirstOrDefault() : All(@where, orderBy, 0, columns, whereArgs.ToArray());
					break;
			}
			return true;
		}


		/// <summary>
		/// Creates a new DbCommand from the sql statement specified and assigns it to the connection specified. 
		/// </summary>
		/// <param name="sql">The SQL statement to create the command for.</param>
		/// <param name="conn">The connection to assign the command to.</param>
		/// <param name="args">The parameter values.</param>
		/// <returns>new DbCommand, ready to rock</returns>
		private DbCommand CreateCommand(string sql, DbConnection conn, params object[] args)
		{
			var result = _factory.CreateCommand();
			if(result != null)
			{
				result.Connection = conn;
				result.CommandText = sql;
				result.AddParams(args);
			}
			return result;
		}


		/// <summary>
		/// Checks whether there's a column in the table schema of this dynamic model which has the same name as the columnname specified, using a culture invariant, case insensitive
		/// comparison
		/// </summary>
		/// <param name="columnName">Name of the column.</param>
		/// <returns></returns>
		private bool ColumnExists(string columnName)
		{
			return this.Schema.Any(c => string.Compare(this.GetColumnName(c), columnName, StringComparison.InvariantCultureIgnoreCase) == 0);
		}


		/// <summary>
		/// Builds the paged result.
		/// </summary>
		/// <param name="sql">The SQL statement to build the query pair for. Can be left empty, in which case the table name from the schema is used</param>
		/// <param name="primaryKeyField">The primary key field. Used for ordering. If left empty the defined PK field is used</param>
		/// <param name="where">The where clause. Default is empty string.</param>
		/// <param name="orderBy">The order by clause. Default is empty string.</param>
		/// <param name="columns">The columns to use in the project. Default is '*' (all columns, in table defined order).</param>
		/// <param name="pageSize">Size of the page. Default is 20</param>
		/// <param name="currentPage">The current page. 1-based. Default is 1.</param>
		/// <param name="args">The values to use as parameters.</param>
		/// <returns>The result of the paged query. Result properties are Items, TotalPages, and TotalRecords.</returns>
		private dynamic BuildPagedResult(string sql = "", string primaryKeyField = "", string where = "", string orderBy = "", string columns = "*", int pageSize = 20, int currentPage = 1,
										 params object[] args)
		{
			var queryPair = this.BuildPagingQueryPair(sql, primaryKeyField, where, orderBy, columns, pageSize, currentPage, args);
			dynamic result = new ExpandoObject();
			result.TotalRecords = Scalar(queryPair.CountQuery, args);
			result.TotalPages = result.TotalRecords / pageSize;
			if(result.TotalRecords % pageSize > 0)
			{
				result.TotalPages += 1;
			}
			result.Items = Query(string.Format(queryPair.MainQuery, columns, TableName), args);
			return result;
		}


		/// <summary>
		/// Builds the select query pattern using the where, orderby and limit specified. 
		/// </summary>
		/// <param name="where">The where.</param>
		/// <param name="orderBy">The order by.</param>
		/// <param name="limit">The limit.</param>
		/// <returns>Select statement pattern with {0} and {1} ready to be filled with projection list and source.</returns>
		private string BuildSelectQueryPattern(string where, string orderBy, int limit)
		{
			string sql = this.GetSelectQueryPattern(limit);
			if(!string.IsNullOrEmpty(where))
			{
				sql += where.Trim().StartsWith("WHERE", StringComparison.OrdinalIgnoreCase) ? where : " WHERE " + where;
			}
			if(!String.IsNullOrEmpty(orderBy))
			{
				sql += orderBy.Trim().StartsWith("ORDER BY", StringComparison.OrdinalIgnoreCase) ? orderBy : " ORDER BY " + orderBy;
			}
			return sql;
		}
		

		/// <summary>
		/// Gets the pk comparison predicate query fragment, which is PrimaryKeyField = [parameter]
		/// </summary>
		/// <returns>ready to use predicate which assumes parameter to use for value is the first parameter</returns>
		private string GetPkComparisonPredicateQueryFragment()
		{
			return string.Format("{0} = {1}", this.PrimaryKeyField, this.PrefixParameterName("0"));
		}



		/// <summary>
		/// Gets the column definition of the column specified. This is a dynamic which contains all the fields of the schema row obtained for this table. 
		/// </summary>
		/// <param name="columnName">Name of the column.</param>
		/// <returns></returns>
		private dynamic GetColumn(string columnName)
		{
			return this.Schema.FirstOrDefault(c => string.Compare(this.GetColumnName(c), columnName, StringComparison.InvariantCultureIgnoreCase) == 0);
		}


		#region OBSOLETE CRUFT
#warning SEE #233.
		[Obsolete("Candidate for removal because it's buggy and doesn't validate for currency but for decimal (no scale check)")]
		public virtual void ValidateIsCurrency(object value, string message = "Should be money")
		{
			if(value == null)
				Errors.Add(message);
			decimal val = decimal.MinValue;
			decimal.TryParse(value.ToString(), out val);
			if(val == decimal.MinValue)
				Errors.Add(message);
		}
		#endregion


		/// <summary>
		/// List out all the schema bits for use with ... whatever
		/// </summary>
		public IEnumerable<dynamic> Schema
		{
			get { return _schema ?? (_schema = Query(this.TableSchemaQuery, this.TableName).ToList()); }
		}


		/// <summary>
		/// Creates an empty Expando set with defaults from the DB. The default values are in string format.
		/// </summary>
		public dynamic Prototype
		{
			get
			{
				dynamic result = new ExpandoObject();
				var schema = Schema;
				foreach(dynamic column in schema)
				{
					var dc = (IDictionary<string, object>)result;
					dc.Add(this.GetColumnName(column), this.DefaultValue(column));
				}
				result._Table = this;
				return result;
			}
		}

		/// <summary>
		/// Gets or sets the name of the table this dynamicmodel is represented by.
		/// </summary>
		public virtual string TableName { get; set; }
		/// <summary>
		/// Gets or sets the primary key field. If empty, "ID" is used.
		/// </summary>
		public virtual string PrimaryKeyField { get; set; }
		/// <summary>
		/// Gets or sets the descriptor field name, which is useful if the table is a lookup table. Descriptor field is the field containing the textual representation of the value
		/// in PrimaryKeyField.
		/// </summary>
		public string DescriptorField { get; protected set; }
		/// <summary>
		/// Contains the error messages collected since the last Validate.
		/// </summary>
		public IList<string> Errors { get; protected set; }
	}
}