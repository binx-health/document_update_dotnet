using System.Diagnostics;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using ServiceReference;
using static ServiceReference.wsDocumentsSoapClient;
using DotNetEnv;
using Npgsql;
using Microsoft.IdentityModel.Tokens;

PSQLDataAccess db = new PSQLDataAccess();
bool process_data = true;

bool init(){
    //prep the database

    Env.Load();

    string connectionstring = $"Host={getkeystring("PGHOST")}; Port={getkeystring("PGPORT")}; Database={getkeystring("PGDATABASE")}; User Id={getkeystring("PGUSER")}; Password={getkeystring("PGPASSWORD")};";
   

    Console.WriteLine("connection string :: "+connectionstring);

    if(db.connect(connectionstring)){
        try{
            Console.WriteLine("opening database connection - ");
            Console.WriteLine("truncating tables ");
            db.ExecuteNonQueryCommand("truncate documents_raw;");
            db.ExecuteNonQueryCommand("truncate documents_old;");
            Console.WriteLine("transfering data to archive");
            db.ExecuteSqlCommand("insert into documents_old select * from documents;");
            Console.WriteLine("init complete. ");
            return true;
        } catch(Exception ex){
            Console.WriteLine("ERROR :: "+ex.Message);
        }
    }
    return false;
}

async Task<int> main(){

    await GetAndProcessSOAPDocumentData();
   
    if(init())
    {
        Console.WriteLine("got data! lets write the file out");
        await processSOAPOutputToTSVfile("documents");

        Console.WriteLine("copy the data to the database, populate documents_raw!"+System.Environment.NewLine);

        string sqlcommand = string.Join("",File.ReadAllLines($"./documents.tsv"));
        sqlcommand = sqlcommand.Remove(sqlcommand.Length-1,1); //trailing comma
        db.ExecuteSqlCommand(sqlcommand);
        int newdocumentscount = ListNewDocuments(); //NewDocumentProcessing();
        int obsoltetedocumentcount =  ListObsoleteDocuments();//ObsoletedDocumentProcessing();
        int newrevisionpublishedcount = ListNewRevisionDocuments();// NewRevisionDocumentProcessing();
        int newdocumentstitlescount =  ListDocumentTitleUpdateDocuments();// NewDocumentTitleProcessing();

        if(process_data)
        {
            NewDocumentProcessing();
            ObsoletedDocumentProcessing();
            NewRevisionDocumentProcessing();
            NewDocumentTitleProcessing();
        }

        db.disconnect();

        StringBuilder sb = new StringBuilder();
        sb.Append("Data Import Complete : "+System.Environment.NewLine);
        sb.Append(newdocumentscount+" new documents were imported"+System.Environment.NewLine); 
        sb.Append(obsoltetedocumentcount +" documents were marked as obsolete"+System.Environment.NewLine);
        sb.Append(newrevisionpublishedcount + " documents had new revisions released"+System.Environment.NewLine);
        sb.Append(newdocumentstitlescount + " documents had their title revised."+System.Environment.NewLine);
        Console.WriteLine(sb.ToString());
    }
    else{
        Console.WriteLine("Exiting No SOAP DATA Available or Database connection failure");
    }
    return 0;
}


async Task<bool> GetAndProcessSOAPDocumentData()
{
    try{
        var auth = new wsAuthenticator();
        auth.UserName = "training";
        auth.Password = "Passw0rd!";

        var endPointAddress = new EndpointAddress("https://mybinxhealth.qt9qms.app/services/wsDocuments.asmx"); 
        var client = new wsDocumentsSoapClient(EndpointConfiguration.wsDocumentsSoap,endPointAddress);

        GetAllDocumentsAsDataSetResponse response =  await client.GetAllDocumentsAsDataSetAsync(auth, true);
        return processSOAPOutputToXmlFile("documents",response.GetAllDocumentsAsDataSetResult.Nodes);
    }
    catch(Exception ex){
        Console.WriteLine(ex.Message);
    }
    return false;
}

bool processSOAPOutputToXmlFile(string path,List<XElement> nodes){

    var document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"),new XElement("documents",nodes));
    try
    {
        document.Save(path+".xml", SaveOptions.None);
        Console.WriteLine($"XML file successfully saved to {path+".xml"}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving XML file: {ex.Message}");
        return false;
    }
    return true;
}

async Task<bool> processSOAPOutputToTSVfile(string path)
{
    
    try{
        char x = ' ';
        XDocument doc = XDocument.Load(path+".xml");
        var documents = doc.Descendants("Table1").ToList();
        var lines = new List<string>
        {
            "insert into documents_raw (doc_id,documentcode,documentname,rev) values "
        };
        int processed = 0;
        foreach (var document in documents)
        { 
            var docId = document.Element("DocumentID")?.Value ?? "";
            var docCode = document.Element("DocumentCode")?.Value ?? "";
            var docNum =  document.Element("DocNum")?.Value ?? "";
            var docName = document.Element("DocumentName")?.Value;
            var docRev = document.Element("Rev._x0020__x0023_")?.Value ?? "";

            string i = ReplaceAny(docName??"",new char[] { '"', '\'','’','‘' }, x); 
            docName = i.Trim();
            lines.Add($"('{docId}','{docCode+docNum}','{docName}','{docRev}'),");
            processed++;
        }
        await File.WriteAllLinesAsync(path+".tsv", lines);
    }
    catch (Exception)
    {
        return false;
    }
    return true;
}

int ListNewDocuments(){
    int count = 0;
    StringBuilder sb = new StringBuilder().Append("NEW DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,d.documentname,d.rev,now(),'' from documents_raw d WHERE lower(trim(d.doc_id)) NOT IN (SELECT lower(trim(doc_id)) FROM documents);");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {
        
        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            sb.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine); 
            count++; 
        }
        Console.WriteLine(sb.ToString());
    }
    return count;
}

 int NewDocumentProcessing(){
    StringBuilder sql = new StringBuilder();
    sql.Append("insert into documents (doc_id,documentcode,documentname,rev,dateloaded,documenttype) ");
    sql.Append("select d.doc_id,d.documentcode,d.documentname,d.rev,now(),'' from documents_raw d WHERE lower(trim(d.doc_id)) NOT IN (SELECT lower(trim(doc_id)) FROM documents);");
 
    return  db.ExecuteSqlCommand(sql.ToString());
}

int ListObsoleteDocuments(){
    int count = 0;
    StringBuilder sb = new StringBuilder().Append("OBSOLETE DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,d.documentname,d.rev from documents d WHERE d.doc_id not in (select documents_raw.doc_id from documents_raw) and d.active = true;");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {

        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            sb.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine);  
            count++;   
        }
        Console.WriteLine(sb.ToString());
    }
    return count;
}


int ObsoletedDocumentProcessing(){
    StringBuilder sql = new StringBuilder("update documents set active = false where doc_id not in (select documents_raw.doc_id from documents_raw) and active = true;");
    return  db.ExecuteSqlCommand(sql.ToString());
}


int ListNewRevisionDocuments(){
    int count = 0;
    StringBuilder sb = new StringBuilder().Append("NEW REVISION DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,d.documentname,rd.rev from documents d inner join documents_raw rd on rd.doc_id = d.doc_id where d.rev < rd.rev ");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {
        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            sb.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine);  
            count++;   
        }
        Console.WriteLine(sb.ToString());
    }
    return count;
}

int NewRevisionDocumentProcessing(){
    StringBuilder sql = new StringBuilder();
    sql.Append("UPDATE documents  ");
    sql.Append("SET rev = raw.rev, dateloaded = now(),documentname = raw.documentname ");
    sql.Append("FROM documents_raw raw ");
    sql.Append("WHERE documents.doc_id = raw.doc_id AND documents.rev < raw.rev; ");   
    return  db.ExecuteSqlCommand(sql.ToString());
}


int ListDocumentTitleUpdateDocuments(){
    int count = 0;
    StringBuilder sb = new StringBuilder().Append("NEW REVISION DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,rd.documentname,rd.rev from documents d inner join documents_raw rd on rd.doc_id = d.doc_id WHERE trim(d.documentname) != trim(rd.documentname);");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {
        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            sb.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine);  
            count++;   
        }
        Console.WriteLine(sb.ToString());
    }
    return count;
}

int NewDocumentTitleProcessing(){
    StringBuilder sql = new StringBuilder();    
    sql.Append("UPDATE documents  ");
    sql.Append("SET  documentname = raw.documentname, dateloaded = now() ");
    sql.Append("FROM documents_raw raw ");
    sql.Append("WHERE documents.doc_id = raw.doc_id AND documents.documentname <> raw.documentname; "); 
    return  db.ExecuteSqlCommand(sql.ToString());
}

string ReplaceAny(string target, char[] toBeReplaced, char replaceWith)
{
    foreach (char c in toBeReplaced)
    {
        target = target.Replace(c, replaceWith);
    }
    return target;
}

string getkeystring(string key){
    return Environment.GetEnvironmentVariable(key)??"";
}

await main();

