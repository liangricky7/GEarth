using GEarth.Core;
using System.Reflection;
using System.Text.Json;

void Log(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
foreach (bool clinicAccess in new []{true,false})
{
 var w=SocietyFactory.Create("challenge-health",false,1100);
 var nation=w.Society!.Nations["dune"];
 var soldier=w.InCountry("dune").First(a=>a.Occupation=="soldier");
 soldier.Health=20;
 if(!clinicAccess)
 {
  SocietyEconomy.Transfer(w,soldier,w.Agents["dune_government"],SocietyBank.Liquid(w,soldier),"probe_remove_treatment_funding");
  foreach(var clinic in w.InCountry("dune").Where(a=>a.Role=="business" && a.Specialty=="clinic"))
   foreach(var doctor in SocietyLabor.Workers(w,clinic)) doctor.WorkEffort=0;
 }
 if(!clinicAccess)
  foreach(var a in w.InCountry("dune"))
  {
   var units=a.Inventory.GetValueOrDefault("medicine");
   a.Inventory["medicine"]=0;
   w.Society.LostGoods["medicine"]+=units;
  }
 w.Tick=6;
 SocietyEngine.Advance(w);
 Log(new{probe="wound_treatment_dependency",clinicAccess,soldier.Health,fit=!SocietyEconomy.TooSick(soldier)});
}
{
 var w=SocietyFactory.Create("challenge-pay",false,1100);
 var n=w.Society!.Nations["dune"];
 var g=w.Agents["dune_government"];
 var soldier=w.InCountry("dune").First(a=>a.Occupation=="soldier");
 soldier.WorkEffort=0; soldier.ArrearsMinor=0; soldier.WeekWorkDays=7;
 var before=soldier.CashMinor;
 typeof(SocietyEconomy).GetMethod("PublicPayroll",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{w,n,g,new[]{soldier}});
 Log(new{probe="striker_paid_predicate",cash_change=soldier.CashMinor-before,soldier.ArrearsMinor,old_paid=soldier.ArrearsMinor==0,day_effort=SocietyLabor.DayEffort(w,soldier)});
 var recruit=w.InCountry("dune").First(a=>a.Role=="citizen" && a.EmployerId is not null && a.Occupation!="soldier");
 var originalEmployer=recruit.EmployerId;
 recruit.ArrearsMinor=500; recruit.ArrearsWeeks=1;
 SocietyLabor.Hire(w,w.InCountry("dune").First(a=>a.Role=="business" && a.Specialty=="construction"),recruit,"builder");
 Log(new{probe="existing_civilian_hire_inherits_arrears",originalEmployer,new_employer=recruit.EmployerId,recruit.ArrearsMinor,recruit.ArrearsWeeks});
}
Log(new{probe="signed_cession_rule",initial=0.25,after5=0.25*Math.Pow(.75,5),after100=0.25*Math.Pow(.75,100),initialRangeIsInvariant=false});
Log(new{probe="multiwar_goods_share",fiveLossesRemainingFraction=Math.Pow(.9,5),fiveLossesSeizedFraction=1-Math.Pow(.9,5),cashTaxBaseSeizedFraction=5*.1});
