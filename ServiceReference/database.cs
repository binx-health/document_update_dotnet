using Npgsql;
using System;
using System.Diagnostics;
using System.Threading.Tasks;


public class PSQLDataAccess(){
    string PgconnectionString = "Host=localhost; Port=5432; Database=training; User Id=postgres; Password=postgress;";
    NpgsqlConnection? connection = null;

    bool connected = false;

    public bool connect(){
        connection = new NpgsqlConnection(PgconnectionString);
        try{
        connection.Open();
        } catch (Exception ex){
            Debug.WriteLine(ex.Message);
            return connected = false;
        }
        return connected = true;
    }
    public bool connect(string connectionString){
        PgconnectionString = connectionString;
        return connect();
    }

    public bool disconnect(){
        if(connection is not null && connected){
            connection.Close();
        }
        return connected = false;
    }

    public int ExecuteNonQueryCommand(string commandText) {
        try{
                //Debug.WriteLine(commandText);
                NpgsqlCommand command = new NpgsqlCommand(commandText,connection);
                return command.ExecuteNonQuery();
        }
        catch ( System.Data.Common.DbException ex){
            Console.WriteLine(ex.Message);
            return 0;
        }

    }

    public  int ExecuteSqlCommand(string commandText) {
        try{
                //Debug.WriteLine(commandText);
                NpgsqlCommand command = new NpgsqlCommand(commandText,connection);
                return  command.ExecuteNonQuery();
        }
        catch (Exception ex){
            Console.WriteLine(ex.Message);
            return 0;
        }
    }


    public NpgsqlDataReader GetReader(string commandText){
        NpgsqlDataReader reader = new NpgsqlCommand(commandText,connection).ExecuteReader();
        return reader;
    }
}


