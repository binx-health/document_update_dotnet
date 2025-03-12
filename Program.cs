using System.ServiceModel;
using System.Text;
using System.Xml.Linq;
using ServiceReference;
using static ServiceReference.wsDocumentsSoapClient;
using DotNetEnv;
using Npgsql;
using System.Net.Mail;

PSQLDataAccess db = new PSQLDataAccess();
bool process_data = true;
SmtpClient smtpClient= new SmtpClient("mail.gmail.com");
StringBuilder emailReport = new StringBuilder();
string targetEmailAddress = getkeystring("SEND_TO");


bool init(){
     Env.Load();

    string connectionstring = $"Host={getkeystring("PGHOST")}; Port={getkeystring("PGPORT")}; Database={getkeystring("PGDATABASE")}; User Id={getkeystring("PGUSER")}; Password={getkeystring("PGPASSWORD")};";

   // smtpClient.Credentials = new NetworkCredential(getkeystring("GMAIL_ACCOUNT"),getkeystring("GMAIL_APP_PASSWORD"));
   // smtpClient.EnableSsl = true;
    

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
            Console.WriteLine("Processing data.....");
            NewDocumentProcessing();
            ObsoletedDocumentProcessing();
            NewRevisionDocumentProcessing();
            NewDocumentTitleProcessing();
            UpdateNonTrainableDocuments();
        }
        db.disconnect();

        // StringBuilder sb = new StringBuilder();
        // sb.Append("Data Import Complete : "+System.Environment.NewLine);
        // sb.Append(newdocumentscount+" new documents were imported"+System.Environment.NewLine); 
        // sb.Append(obsoltetedocumentcount +" documents were marked as obsolete"+System.Environment.NewLine);
        // sb.Append(newrevisionpublishedcount + " documents had new revisions released"+System.Environment.NewLine);
        // sb.Append(newdocumentstitlescount + " documents had their title revised."+System.Environment.NewLine);
        // //Console.WriteLine(sb.ToString());
    }
    else
    {
        Console.WriteLine("ERROR :: Exiting No SOAP DATA Available or Database connection failure");
    }
    Console.WriteLine(emailReport);
   // await sendEmail();
    return 0;
}

async Task sendEmail(){
    try{
          var emailService = new EmailService(
            getkeystring("SMTP_HOST"),
            int.Parse(getkeystring("SMTP_PORT")),
            getkeystring("GMAIL_ACCOUNT"),
            getkeystring("GMAIL_APP_PASSWORD")
        );

        var mailMessage = new EmailMessage{
            Subject = "Document Import Report",
            Body = emailReport.ToString(),
            IsHtml = false,
            ToAddresses = new[]{"sean.barnes@mybinxhealth.com"}
        };
        //mailMessage.To.Add(targetEmailAddress);
       await emailService.SendEmailAsync(mailMessage);  
    } catch(Exception ex){
        Console.WriteLine("SMTP ERROR :: "+ex.Message);
    }
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
            var docNum =  document.Element("DocNum")?.Value.PadLeft(3,'0') ?? "";
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

void UpdateNonTrainableDocuments(){
    try{
        string sql = "update documents set active = false where id in (select d.id from documents d Where d.documentcode not ILIKE '%-SOP-%' AND d.documentcode not ILIKE '%-POL-%' AND d.documentcode not ILIKE '%-QCP-%'";
        sql += "AND d.documentcode not ILIKE '%-COP-%' AND d.documentcode not ILIKE '%-COSHH-%' AND d.documentcode not ILIKE '%-P-%' AND d.documentcode not ILIKE '%-PRO-%' AND d.documentcode not ILIKE '%-RA-%' AND d.Active = true)";
        db.ExecuteNonQueryCommand(sql);
    }
    catch(Exception e){
        Console.WriteLine(e.Message);
    }
}

int ListNewDocuments(){
    int count = 0;
    emailReport.Append("NEW DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,d.documentname,d.rev,now(),'' from documents_raw d WHERE lower(trim(d.doc_id)) NOT IN (SELECT lower(trim(doc_id)) FROM documents);");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {
        
        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            emailReport.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine); 
            count++; 
        }
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
    emailReport.Append("OBSOLETE DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,d.documentname,d.rev from documents d WHERE d.doc_id not in (select documents_raw.doc_id from documents_raw) and d.active = true;");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {

        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            emailReport.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine);  
            count++;   
        }
    }
    return count;
}


int ObsoletedDocumentProcessing(){
    StringBuilder sql = new StringBuilder("update documents set active = false where doc_id not in (select documents_raw.doc_id from documents_raw) and active = true;");
    return  db.ExecuteSqlCommand(sql.ToString());
}


int ListNewRevisionDocuments(){
    int count = 0;
    emailReport.Append("NEW REVISION DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,d.documentname,rd.rev from documents d inner join documents_raw rd on rd.doc_id::INTEGER= d.doc_id::INTEGER where d.rev::INTEGER < rd.rev::INTEGER; ");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {
        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            emailReport.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine);  
            count++;   
        }
    }
    return count;
}

int NewRevisionDocumentProcessing(){
    StringBuilder sql = new StringBuilder();
    sql.Append("UPDATE documents d SET rev = rd.rev,dateloaded = now(),documentname = rd.documentname ");
    sql.Append("FROM documents_raw rd WHERE d.doc_id::INTEGER = rd.doc_id::INTEGER AND d.rev::INTEGER < rd.rev::INTEGER;");
    // sql.Append("UPDATE documents  ");
    // sql.Append("SET rev = raw.rev, dateloaded = now(),documentname = raw.documentname ");
    // sql.Append("FROM documents_raw raw ");
    // sql.Append("WHERE documents.doc_id = raw.doc_id AND documents.rev < raw.rev; ");   
    return  db.ExecuteSqlCommand(sql.ToString());
}


int ListDocumentTitleUpdateDocuments(){
    int count = 0;
    emailReport.Append("NEW TITLE DOCUMENTS"+System.Environment.NewLine);
    StringBuilder sql = new StringBuilder();    
    sql.Append("select d.doc_id,d.documentcode,rd.documentname,rd.rev from documents d inner join documents_raw rd on rd.doc_id::INTEGER = d.doc_id::INTEGER WHERE trim(d.documentname) != trim(rd.documentname);");
    using(NpgsqlDataReader dr = db.GetReader(sql.ToString()))
    {
        while (dr.Read()){
            var docId = dr["doc_id"] ?? "";
            var docCode = dr["documentcode"] ?? "";
            var docName = dr["documentname"] ?? "";
            var docRev = dr["rev"] ?? "";
            emailReport.Append(docId+"\t"+docCode+"\t"+docName+"\t"+docRev+System.Environment.NewLine);  
            count++;   
        }
    }
    return count;
}

int NewDocumentTitleProcessing(){
    StringBuilder sql = new StringBuilder();    
    sql.Append("UPDATE documents  ");
    sql.Append("SET  documentname = raw.documentname, dateloaded = now() ");
    sql.Append("FROM documents_raw raw ");
    sql.Append("WHERE documents.doc_id::INTEGER = raw.doc_id::INTEGER AND documents.documentname <> raw.documentname; "); 
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

