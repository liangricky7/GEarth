import { CONTRACT } from '../client.mjs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

export function pipelineFixture() {
  const coords = [['aster',40,-105],['beryl',-18,-58],['cinder',50,15],['dune',25,42],['ember',-27,135],
    ['fjord',64,-22],['grove',3,23],['haven',25,110],['ionia',8,80],['jade',35,140]];
  const countries = coords.map(([id,lat,lon]) => ({ id, name: id.toUpperCase(), lat, lon, government_id: 'monarchy' }));
  const routes = [['aster','beryl',2],['beryl','jade',4],['aster','jade',4]].map(([from,to,travel_ticks]) =>
    ({ id: `${from}_to_${to}`, from: `${from}_city`, to: `${to}_city`, travel_ticks, open: true }));
  const plan = edges => ({ edge_ids: edges, travel_ticks: edges.reduce((n,id) => n+routes.find(r => r.id===id).travel_ticks,0) });
  const rows = [
    [0,'run_started',[],{}],
    [1,'shipment_dispatched',['cargo_1'],{good:'food',quantity:5,plan:plan(['aster_to_beryl','beryl_to_jade'])}],
    [2,'conflict_started',['aster:jade','aster'],{first:'aster',second:'jade'}],
    [3,'diplomatic_offer',['jade','aster:jade'],{proposed_by:'jade',proposal_kind:'peace',proposal_expires_tick:63}],
    [3,'shipment_rerouted',['cargo_1'],{plan:plan(['beryl_to_jade'])}],
    [4,'diplomatic_response',['aster:jade'],{accept:true,at_war:false,trade_agreement:false,tension:25}],
    [5,'shipment_failed',['cargo_1'],{reason:'DELIVERY_DEADLINE',good:'food',quantity:5}],
    [6,'shipment_dispatched',['cargo_2'],{good:'tools',quantity:3,plan:plan(['aster_to_jade'])}],
    [9,'shipment_delivered',['cargo_2'],{good:'tools',quantity:3}],
    [15,'run_completed',[],{}],
  ];
  const events = rows.map(([tick,event_type,entity_ids,payload],i) => ({ schema_version:1,run_id:'integration',
    event_id:`integration:e${i+1}`,seq:i+1,tick,event_type,entity_ids,payload,cause_ids:[],source:'rule' }));
  const frames = [0,7,14,15].map((tick,i) => ({ schema_version:CONTRACT,run_id:'integration',revision:i+1,tick,
    week:Math.ceil(tick/7),frame_kind:i===0?'opening':i===3?'terminal':'week',complete_week:tick>0&&tick%7===0,
    through_event_sequence:events.filter(e=>e.tick<=tick).length,
    units:{days_per_tick:1,days_per_week:7,minor_units_per_wld:100},
    snapshot:{run:{id:'integration',mode:'RULE',status:i===3?'completed':'running'},countries,
      locations:countries.map(c=>({id:`${c.id}_city`,country_id:c.id})),routes,
      nations:countries.map(c=>({id:c.id,population:20,unemployed:i,approval:.7,gini_cash:.2})),
      relationships:[{id:'aster:jade',first:'aster',second:'jade',at_war:false,proposed_by:null}],
      agents:i<2?[{id:'worker',role:'citizen',country_id:'aster',cash_minor:123,deposit_minor:456}]:[],
      banks:[{bank_id:'bank',state:{reserves_minor:10000}}],loans:[],metrics:{cash_minor:10123},
      shipments:tick===7?[{id:'cargo_2',good:'tools',quantity:3,status:'in_transit',edge_index:0,edge_remaining_ticks:2,
        plan:plan(['aster_to_jade'])}]:[] } }));
  const metadata={schema_version:CONTRACT,run_id:'integration',status:'completed',captured_from_opening:true,
    first_available_tick:0,checkpoint_revision:4,checkpoint_tick:15};
  return { metadata, frames, events };
}

if (process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const {frames,events,metadata}=pipelineFixture(), snapshot=frames[0].snapshot;
  const countries=new Map(snapshot.locations.map(l=>[l.id,l.country_id]));
  console.log(JSON.stringify({format:'faultline.events.v1',run_id:metadata.run_id,countries:snapshot.countries,
    routes:snapshot.routes.map(r=>({...r,from:countries.get(r.from),to:countries.get(r.to)})),events}));
}
