using System.Diagnostics;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using ServiceReference;
using static ServiceReference.wsDocumentsSoapClient;

PSQLDataAccess db = new PSQLDataAccess();

bool init(){
    //prep the database
    if(db.connect()){
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

        Console.WriteLine("copy the data to the database, populate documents_raw!");
        //db.ExecuteNonQueryCommand("copy documents_raw FROM './documents.tsv' WITH DELIMITER E'\\t' null as ';'");
        string sqlcommand = string.Join("",File.ReadAllLines($"./documents.tsv"));
        sqlcommand = sqlcommand.Remove(sqlcommand.Length-1,1); //trailing comma
        db.ExecuteSqlCommand(sqlcommand);
        int newdocumentscount =  NewDocumentProcessing();
        int obsoltetedocumentcount =  ObsoletedDocumentProcessing();
        int newrevisionpublishedcount =  NewRevisionDocumentProcessing();
        int newdocumentstitlescount =  NewDocumentTitleProcessing();

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
        Console.WriteLine("Exiting No SOAP DATA Available.");
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
            //"doc_id\tdocumentcode\tdocumentname\trev"
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

            string i = ReplaceAny(docName??"",new char[] { '"', '\'','’','‘' }, x); //"CO-LAB-FRM-086","‘0260 CT Forward Primer from SGS DNA’","6","Laboratory","Forms","QMS","Corporate","QS-IQC-0260","Active"
            //string o = ReplaceAny(i, new char[] { '‘', ' ' }, x);
            //string j = ReplaceAny(o, new char[] {','}, '\t');
            docName = i.Trim();
            //lines.Add($"{docId}\t{docCode+docNum}\t{docName}\t{docRev}");
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

 int NewDocumentProcessing(){
     Console.WriteLine("NEW :");
    StringBuilder sql = new StringBuilder();
    sql.Append("insert into documents (doc_id,documentcode,documentname,rev,dateloaded,documenttype) ");
    sql.Append("select doc_id,documentcode,documentname,rev,now(),'' from documents new WHERE lower(trim(new.doc_id)) NOT IN ");
    sql.Append("(SELECT lower(trim(doc_id)) FROM documents_old);");   
    return  db.ExecuteSqlCommand(sql.ToString());
}

int ObsoletedDocumentProcessing(){
     Console.WriteLine("OBS :");
    StringBuilder sql = new StringBuilder("update documents set active = false where doc_id not in (select documents_raw.doc_id from documents_raw) and active = true;");
    return  db.ExecuteSqlCommand(sql.ToString());
}

int NewRevisionDocumentProcessing(){
     Console.WriteLine("UPREV :");
    StringBuilder sql = new StringBuilder();
    sql.Append("UPDATE documents  ");
    sql.Append("SET rev = raw.rev, dateloaded = now(),documentname = raw.documentname ");
    sql.Append("FROM documents_raw raw ");
    sql.Append("WHERE documents.doc_id = raw.doc_id AND documents.rev < raw.rev; ");   
    return  db.ExecuteSqlCommand(sql.ToString());
}

int NewDocumentTitleProcessing(){
     Console.WriteLine("TITLE :");
    StringBuilder sql = new StringBuilder();    
    sql.Append("UPDATE documents  ");
    sql.Append("SET  documentname = raw.documentname, dateloaded = now() ");
    sql.Append("FROM documents_raw raw ");
    sql.Append("WHERE documents.doc_id = raw.doc_id AND documents.documentname < raw.documentname; "); 
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

await main();

